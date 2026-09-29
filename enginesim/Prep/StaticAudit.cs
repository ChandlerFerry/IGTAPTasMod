using Mono.Cecil;
using Mono.Cecil.Cil;

// Build-time check for mutable statics (enginesim/README.md, "per-thread statics"). A static that game code writes
// outside a type's static constructor, or whose contents it mutates, holds one world's state; shared between solver
// threads, the last world loaded wins. Every such field must be per thread (`Prep.ThreadStaticFields`) or named in
// enginesim/static-allow.txt as `Type::field  # reason` (a listed field stays shared, so a reason is required).
// The audit runs on the rewritten module (after cuts.txt and hooks.txt emptied their methods), so writes inside an
// emptied method are gone, and on the shim module Prep loads: the shim's own statics are `[ThreadStatic]` in their
// source or allowed here. Engine-side writes are outside Prep's view: `World.Load` restores every game static per
// thread from the export, which is what makes the per-thread copy correct.
static class StaticAudit
{
    public sealed record Offense(FieldDefinition Field)
    {
        public List<string> Sites { get; } = new(); // "Type::Method" of each write or mutation site
        public bool Written { get; set; }   // stsfld outside any .cctor
        public bool Mutated { get; set; }   // contents mutated (stelem, stfld, stobj, a call through it)
    }

    /// <param name="Fields">Every static field of the audited modules.</param>
    public sealed record Report(Dictionary<string, FieldDefinition> Fields, List<Offense> Offenses);

    public static string Key(FieldDefinition f) => f.DeclaringType.FullName + "::" + f.Name;

    static string Key(FieldReference f) => f.DeclaringType.GetElementType().FullName + "::" + f.Name;

    public static Report Analyze(ModuleDefinition module, ModuleDefinition? other)
    {
        var modules = other == null ? new List<ModuleDefinition> { module } : new List<ModuleDefinition> { module, other };
        var fields = new Dictionary<string, FieldDefinition>();
        foreach (ModuleDefinition m in modules)
            foreach (TypeDefinition type in m.GetTypes())
                foreach (FieldDefinition f in type.Fields)
                    if (f.IsStatic) fields.TryAdd(Key(f), f);
        var offenses = new Dictionary<string, Offense>();
        foreach (ModuleDefinition m in modules)
            foreach (TypeDefinition type in m.GetTypes())
                foreach (MethodDefinition method in type.Methods)
                    if (method.HasBody) Scan(method, fields, offenses);
        return new Report(fields, offenses.Values.OrderBy(o => Key(o.Field), StringComparer.Ordinal).ToList());
    }

    static Offense Offend(Dictionary<string, Offense> offenses, FieldDefinition f)
    {
        if (!offenses.TryGetValue(Key(f), out Offense? o)) offenses[Key(f)] = o = new Offense(f);
        return o;
    }

    static void Scan(MethodDefinition m, Dictionary<string, FieldDefinition> fields, Dictionary<string, Offense> offenses)
    {
        bool cctor = m.IsStatic && m.IsConstructor;
        string site = m.DeclaringType.FullName + "::" + m.Name;
        foreach (Instruction ins in m.Body.Instructions)
        {
            if (ins.OpCode.Code != Code.Stsfld || ins.Operand is not FieldReference fr) continue;
            if (cctor || !fields.TryGetValue(Key(fr), out FieldDefinition? f)) continue;
            Offense o = Offend(offenses, f);
            o.Written = true;
            if (!o.Sites.Contains(site)) o.Sites.Add(site);
        }
        Mutations(m, fields, offenses, site);
    }

    // Walks the IL with an origin set per stack slot and local ("this value comes from static field F") and records a
    // field as mutated when a store or a call runs through it. Origins propagate through field and element reads,
    // casts and calls' return values, so mutating an object reached from a static counts too. Anything ambiguous (a
    // call's argument, say) is treated as a mutation: over-reporting a field only asks for an explicit per-thread or
    // allow-list decision, missing one keeps a shared bug.
    static void Mutations(MethodDefinition m, Dictionary<string, FieldDefinition> fields, Dictionary<string, Offense> offenses, string site)
    {
        Mono.Collections.Generic.Collection<Instruction> insns = m.Body.Instructions;
        IList<VariableDefinition> vars = m.Body.Variables;
        int n = insns.Count;
        var index = new Dictionary<Instruction, int>(n);
        for (int i = 0; i < n; i++) index[insns[i]] = i;
        var entry = new Flow?[n];
        var work = new Queue<int>();
        void Enqueue(int at, Flow flow)
        {
            if (at < 0 || at >= n) return;
            if (entry[at] == null) { entry[at] = flow; work.Enqueue(at); }
            else if (entry[at]!.Merge(flow)) work.Enqueue(at);
        }
        Enqueue(0, new Flow());
        foreach (ExceptionHandler h in m.Body.ExceptionHandlers)
        {
            var handler = new Flow();
            handler.Stack.Add(new HashSet<FieldDefinition>());
            if (h.HandlerStart != null) Enqueue(index[h.HandlerStart], handler.Clone());
            if (h.FilterStart != null) Enqueue(index[h.FilterStart], handler.Clone());
        }
        while (work.Count > 0)
        {
            int i = work.Dequeue();
            Flow state = entry[i]!.Clone();
            Instruction ins = insns[i];
            Transfer(ins, state, vars, fields, offenses, site);
            if (ins.OpCode.Code is Code.Leave or Code.Leave_S) state.Stack.Clear();
            if (ins.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw) continue;
            if (ins.OpCode.FlowControl == FlowControl.Branch)
            {
                if (ins.Operand is Instruction target) Enqueue(index[target], state);
                continue;
            }
            if (ins.OpCode.FlowControl == FlowControl.Cond_Branch)
            {
                if (ins.Operand is Instruction target) Enqueue(index[target], state.Clone());
                else if (ins.Operand is Instruction[] targets)
                    foreach (Instruction t in targets) Enqueue(index[t], state.Clone());
                if (i + 1 < n) Enqueue(i + 1, state);
                continue;
            }
            if (i + 1 < n) Enqueue(i + 1, state);
        }
    }

    sealed class Flow
    {
        public readonly Dictionary<VariableDefinition, HashSet<FieldDefinition>> Locals = new();
        public readonly List<HashSet<FieldDefinition>> Stack = new();

        public Flow Clone()
        {
            var copy = new Flow();
            foreach (var (v, s) in Locals) copy.Locals[v] = new HashSet<FieldDefinition>(s);
            foreach (HashSet<FieldDefinition> s in Stack) copy.Stack.Add(new HashSet<FieldDefinition>(s));
            return copy;
        }

        public bool Merge(Flow other)
        {
            bool changed = false;
            foreach (var (v, s) in other.Locals)
            {
                if (!Locals.TryGetValue(v, out HashSet<FieldDefinition>? mine)) { Locals[v] = new HashSet<FieldDefinition>(s); changed |= s.Count > 0; }
                else changed |= Union(mine, s);
            }
            for (int i = 0; i < other.Stack.Count; i++)
            {
                while (Stack.Count <= i) { Stack.Add(new HashSet<FieldDefinition>()); changed = true; }
                changed |= Union(Stack[i], other.Stack[i]);
            }
            return changed;
        }

        static bool Union(HashSet<FieldDefinition> target, HashSet<FieldDefinition> add)
        {
            int before = target.Count;
            target.UnionWith(add);
            return target.Count != before;
        }

        public HashSet<FieldDefinition> Pop()
        {
            if (Stack.Count == 0) return new HashSet<FieldDefinition>();
            HashSet<FieldDefinition> top = Stack[^1];
            Stack.RemoveAt(Stack.Count - 1);
            return top;
        }

        public void Push(HashSet<FieldDefinition> set) => Stack.Add(set);
    }

    static void Transfer(Instruction ins, Flow state, IList<VariableDefinition> vars,
        Dictionary<string, FieldDefinition> fields, Dictionary<string, Offense> offenses, string site)
    {
        HashSet<FieldDefinition> Origin(object? operand) =>
            operand is FieldReference fr && fields.TryGetValue(Key(fr), out FieldDefinition? f)
                ? new HashSet<FieldDefinition> { f } : new HashSet<FieldDefinition>();
        VariableDefinition Local(object? operand, int slot) => operand as VariableDefinition ?? vars[slot];
        void Record(HashSet<FieldDefinition> set)
        {
            foreach (FieldDefinition f in set)
            {
                if (Unmutable(f)) continue;
                Offense o = Offend(offenses, f);
                o.Mutated = true;
                if (!o.Sites.Contains(site)) o.Sites.Add(site);
            }
        }

        // Whole families by name: every stelem.*, stind.*, ldind.* and ldelem.* (ldelema too).
        string name = ins.OpCode.Name;
        if (name.StartsWith("stelem", StringComparison.Ordinal))
        {
            state.Pop(); // value
            state.Pop(); // index
            Record(state.Pop()); // array
            return;
        }
        if (name.StartsWith("stind.", StringComparison.Ordinal))
        {
            state.Pop(); // value
            Record(state.Pop()); // address
            return;
        }
        if (name.StartsWith("ldind.", StringComparison.Ordinal))
        {
            state.Push(state.Pop()); // the object reached is still the field's contents
            return;
        }
        if (name.StartsWith("ldelem", StringComparison.Ordinal))
        {
            state.Pop(); // index
            state.Push(state.Pop()); // the element is still the array's contents
            return;
        }
        switch (ins.OpCode.Code)
        {
            case Code.Ldsfld:
            case Code.Ldsflda:
                state.Push(Origin(ins.Operand));
                return;
            case Code.Stsfld:
                state.Pop();
                return;
            case Code.Stloc:
            case Code.Stloc_S:
                state.Locals[Local(ins.Operand, 0)] = state.Pop();
                return;
            case Code.Stloc_0:
            case Code.Stloc_1:
            case Code.Stloc_2:
            case Code.Stloc_3:
                state.Locals[Local(null, ins.OpCode.Code - Code.Stloc_0)] = state.Pop();
                return;
            case Code.Ldloc:
            case Code.Ldloc_S:
            case Code.Ldloca:
            case Code.Ldloca_S:
            {
                VariableDefinition v = Local(ins.Operand, 0);
                state.Push(state.Locals.TryGetValue(v, out HashSet<FieldDefinition>? s) ? new HashSet<FieldDefinition>(s) : new HashSet<FieldDefinition>());
                return;
            }
            case Code.Ldloc_0:
            case Code.Ldloc_1:
            case Code.Ldloc_2:
            case Code.Ldloc_3:
            {
                VariableDefinition v = Local(null, ins.OpCode.Code - Code.Ldloc_0);
                state.Push(state.Locals.TryGetValue(v, out HashSet<FieldDefinition>? s) ? new HashSet<FieldDefinition>(s) : new HashSet<FieldDefinition>());
                return;
            }
            case Code.Dup:
                state.Push(state.Stack.Count == 0 ? new HashSet<FieldDefinition>() : new HashSet<FieldDefinition>(state.Stack[^1]));
                return;
            case Code.Pop:
                state.Pop();
                return;
            case Code.Stfld:
                state.Pop(); // value
                Record(state.Pop()); // object: its field is written
                return;
            case Code.Stobj:
            case Code.Cpobj:
            case Code.Initobj:
                state.Pop();
                Record(state.Pop());
                return;
            case Code.Ldfld:
            case Code.Ldflda:
            case Code.Ldobj:
                state.Push(state.Pop()); // the object reached is still the field's contents
                return;
            case Code.Castclass:
            case Code.Isinst:
            case Code.Unbox:
            case Code.Unbox_Any:
            case Code.Conv_I:
            case Code.Conv_Ovf_I:
            case Code.Conv_Ovf_I_Un:
            case Code.Conv_U:
            case Code.Conv_Ovf_U:
            case Code.Conv_Ovf_U_Un:
            case Code.Conv_I1:
            case Code.Conv_I2:
            case Code.Conv_I4:
            case Code.Conv_I8:
            case Code.Conv_U1:
            case Code.Conv_U2:
            case Code.Conv_U4:
            case Code.Conv_U8:
                state.Push(state.Pop());
                return;
            case Code.Ldftn:
                state.Push(new HashSet<FieldDefinition>());
                return;
            case Code.Ldvirtftn:
                state.Pop();
                state.Push(new HashSet<FieldDefinition>());
                return;
            case Code.Box:
            case Code.Ldlen:
            case Code.Newarr:
                state.Pop();
                state.Push(new HashSet<FieldDefinition>());
                return;
            case Code.Newobj:
            {
                var ctor = (MethodReference)ins.Operand;
                Record(Args(state, ctor.Parameters.Count)); // a constructor may mutate what it is handed
                state.Push(new HashSet<FieldDefinition>());
                return;
            }
            case Code.Call:
            case Code.Callvirt:
            {
                var method = (MethodReference)ins.Operand;
                bool hasThis = ins.OpCode.Code == Code.Callvirt || method.HasThis;
                var args = new List<HashSet<FieldDefinition>>();
                for (int k = 0; k < method.Parameters.Count; k++) args.Add(state.Pop());
                HashSet<FieldDefinition> self = hasThis ? state.Pop() : new HashSet<FieldDefinition>();
                if ((self.Count > 0 && !PureRead(method.Name)) || args.Any(a => a.Count > 0))
                {
                    Record(self);
                    foreach (HashSet<FieldDefinition> a in args) Record(a);
                }
                if (method.ReturnType.MetadataType != MetadataType.Void)
                {
                    var result = new HashSet<FieldDefinition>(self);
                    foreach (HashSet<FieldDefinition> a in args) result.UnionWith(a);
                    state.Push(result);
                }
                return;
            }
            default:
            {
                for (int k = PopCount(ins.OpCode.StackBehaviourPop); k > 0; k--) state.Pop();
                for (int k = PushCount(ins.OpCode.StackBehaviourPush); k > 0; k--) state.Push(new HashSet<FieldDefinition>());
                return;
            }
        }
    }

    // A call's arguments: the union of the trailing slots (Newobj, which has no receiver).
    static HashSet<FieldDefinition> Args(Flow state, int count)
    {
        var union = new HashSet<FieldDefinition>();
        for (int k = 0; k < count; k++) union.UnionWith(state.Pop());
        return union;
    }

    // Reads that cannot mutate what they touch.
    static bool PureRead(string name) =>
        name.StartsWith("get_", StringComparison.Ordinal)
        || name is "ToString" or "GetHashCode" or "Equals" or "GetType" or "Contains" or "ContainsKey" or "ContainsValue"
            or "TryGetValue" or "IndexOf" or "LastIndexOf" or "GetEnumerator" or "MoveNext";

    // Nothing can mutate these through a reference: a string's text and a delegate's invocation list only change when
    // the field itself is written (a stsfld site). A readonly value-type static is copied before any call, and C# only
    // writes it in a constructor, so calls and arguments cannot reach its storage either.
    static bool Unmutable(FieldDefinition f)
    {
        TypeReference t = f.FieldType;
        if (t.FullName == "System.String") return true;
        if (f.IsInitOnly && t.IsValueType) return true;
        try
        {
            for (TypeDefinition? b = t.Resolve()?.BaseType?.Resolve(); b != null; b = b.BaseType?.Resolve())
                if (b.FullName == "System.MulticastDelegate") return true;
        }
        catch (Exception) { } // a base type the resolver cannot reach: assume mutable
        return false;
    }

    static int PopCount(StackBehaviour b) => b switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
            or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
            or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
        StackBehaviour.PopAll => 16,
        _ => 0,
    };

    static int PushCount(StackBehaviour b) => b switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8
            or StackBehaviour.Pushref => 1,
        StackBehaviour.Push1_push1 => 2,
        _ => 0,
    };

    // Every offense must be per thread or allowed; every allow-list entry must name a real static field.
    public static void Check(Report report, Dictionary<string, string> allowed)
    {
        var unknown = allowed.Keys.Where(k => !report.Fields.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException("static-allow.txt names no static field: " + string.Join(", ", unknown));
        var shared = new List<string>();
        foreach (Offense o in report.Offenses)
        {
            string key = Key(o.Field);
            if (o.Field.CustomAttributes.Any(a => a.AttributeType.FullName == "System.ThreadStaticAttribute")) continue;
            if (allowed.ContainsKey(key)) continue;
            string kind = o.Written && o.Mutated ? "written and mutated" : o.Written ? "written outside .cctor" : "contents mutated";
            shared.Add("  " + key + "  (" + kind + " at " + string.Join(", ", o.Sites) + ")");
        }
        if (shared.Count == 0) return;
        throw new InvalidOperationException("Prep: " + shared.Count + " mutable statics are shared between solver threads and neither"
            + " per thread nor in enginesim/static-allow.txt:\n" + string.Join("\n", shared)
            + "\nEvery static field game code writes outside a type's static constructor, or whose contents it mutates, must be"
            + " per thread (Prep's ThreadStaticFields) or listed in static-allow.txt as `Type::field  # reason`."
            + "\nPer-world state goes per thread; listing a field keeps it shared between solver threads, so only list fields whose"
            + " value every world agrees on (or that no run reads).");
    }
}
