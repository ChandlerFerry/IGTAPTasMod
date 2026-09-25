using Mono.Cecil;
using Mono.Cecil.Cil;

static class Prep
{
    public const string ShimName = "IGTAP.UnityShim";

    public static bool Replaced(string name) =>
        name.StartsWith("UnityEngine", StringComparison.Ordinal) || name.StartsWith("Unity.", StringComparison.Ordinal)
        || name is "com.rlabrecque.steamworks.net" or "DOTween" or "Assembly-CSharp-firstpass";

    static int Main(string[] args)
    {
        try
        {
            switch (args.FirstOrDefault())
            {
                case "retarget": return Retarget(args[1], args[2], args[3], args[4], args[5]);
                case "surface": return Surface(args[1], args[2], args[3]);
                case "monofloat": return MonoFloatTypes(args[1], args.Skip(2).ToArray());
                default:
                    Console.Error.WriteLine("usage: retarget <managedDir> <outDir> <cuts.txt> <hooks.txt> <shim.dll> | surface <managedDir> <cuts.txt> <entries.txt>");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    static IEnumerable<string> Lines(string path) =>
        File.ReadAllLines(path).Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0);

    static bool Matches(HashSet<string> set, MethodDefinition m) =>
        set.Contains(m.DeclaringType.FullName + "::" + m.Name) || set.Contains(m.DeclaringType.FullName + "::" + m.Name + "(" + m.Parameters.Count + ")");

    static AssemblyDefinition Load(string managedDir, string file)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managedDir);
        return AssemblyDefinition.ReadAssembly(Path.Combine(managedDir, file), new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false });
    }

    static int Retarget(string managedDir, string outDir, string cutsPath, string hooksPath, string shimPath)
    {
        var cuts = Lines(cutsPath).ToHashSet();
        var used = new HashSet<string>();
        AssemblyDefinition game = Load(managedDir, "Assembly-CSharp.dll");
        ModuleDefinition module = game.MainModule;
        TypeDefinition hookType = ModuleDefinition.ReadModule(shimPath).GetType("IGTAP.EngineSim.Hooks")
            ?? throw new InvalidOperationException("IGTAP.EngineSim.Hooks is missing from " + shimPath);
        var hooks = Lines(hooksPath).Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var shim = new AssemblyNameReference(ShimName, new Version(1, 0, 0, 0));
        module.AssemblyReferences.Add(shim);
        foreach (TypeReference t in module.GetTypeReferences())
            if (t.Scope is AssemblyNameReference a && Replaced(a.Name)) t.Scope = shim;
        foreach (AssemblyNameReference a in module.AssemblyReferences.Where(a => Replaced(a.Name)).ToList())
            module.AssemblyReferences.Remove(a);
        foreach (TypeDefinition type in module.GetTypes())
        {
            StripEngineAttributes(type.CustomAttributes);
            foreach (FieldDefinition f in type.Fields) StripEngineAttributes(f.CustomAttributes);
            foreach (PropertyDefinition p in type.Properties) StripEngineAttributes(p.CustomAttributes);
            foreach (MethodDefinition m in type.Methods)
            {
                StripEngineAttributes(m.CustomAttributes);
                if (!m.HasBody || !Matches(cuts, m)) continue;
                used.Add(m.DeclaringType.FullName + "::" + m.Name);
                Empty(m);
            }
        }
        foreach (string[] hook in hooks)
        {
            string[] target = hook[1].Split("::");
            MethodDefinition method = module.GetType(target[0])?.Methods.SingleOrDefault(m => m.Name == target[1])
                ?? throw new InvalidOperationException("hooks.txt: no single method " + hook[1]);
            MethodDefinition handler = hookType.Methods.SingleOrDefault(m => m.Name == hook[2])
                ?? throw new InvalidOperationException("hooks.txt: no hook " + hook[2]);
            if (handler.Parameters.Count != method.Parameters.Count + 1)
                throw new InvalidOperationException("hooks.txt: " + hook[2] + " must take (self, " + method.Parameters.Count + " args)");
            InjectHook(method, module.ImportReference(handler), hook[0] == "replace");
        }
        int widened = 0;
        foreach (TypeDefinition type in module.GetTypes())
            foreach (MethodDefinition m in type.Methods) widened += MonoFloat.Apply(m);
        StripEngineAttributes(game.CustomAttributes);
        StripEngineAttributes(module.CustomAttributes);
        var unused = cuts.Where(c => !used.Contains(c.Split('(')[0])).ToList();
        if (unused.Count > 0) throw new InvalidOperationException("cuts.txt names methods that do not exist: " + string.Join(", ", unused));
        Directory.CreateDirectory(outDir);
        game.Write(Path.Combine(outDir, "Assembly-CSharp.dll"));
        Console.WriteLine("Retargeted Assembly-CSharp -> " + outDir + " (" + used.Count + " methods cut, " + widened + " float loads widened)");
        return 0;
    }

    static int MonoFloatTypes(string dll, string[] typeNames)
    {
        string temp = dll + ".tmp";
        int widened = 0;
        using (AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { ReadSymbols = false }))
        {
            if (asm.MainModule.GetType("IGTAP.MonoFloatApplied") != null) return 0;
            foreach (string name in typeNames)
            {
                TypeDefinition type = asm.MainModule.GetType(name) ?? throw new InvalidOperationException("No type " + name + " in " + dll);
                foreach (MethodDefinition m in type.Methods) widened += MonoFloat.Apply(m);
            }
            asm.MainModule.Types.Add(new TypeDefinition("IGTAP", "MonoFloatApplied", TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed,
                asm.MainModule.TypeSystem.Object));
            asm.Write(temp);
        }
        File.Copy(temp, dll, true);
        File.Delete(temp);
        Console.WriteLine("Mono float semantics applied to " + typeNames.Length + " types in " + Path.GetFileName(dll) + " (" + widened + " float loads widened)");
        return 0;
    }

    static void StripEngineAttributes(Mono.Collections.Generic.Collection<CustomAttribute> attributes)
    {
        for (int i = attributes.Count - 1; i >= 0; i--)
        {
            TypeReference t = attributes[i].AttributeType;
            if (t.Scope is AssemblyNameReference a && (Replaced(a.Name) || a.Name == ShimName)) attributes.RemoveAt(i);
            else if (attributes[i].HasConstructorArguments && attributes[i].ConstructorArguments.Any(arg => arg.Type.Scope is AssemblyNameReference s && (Replaced(s.Name) || s.Name == ShimName)))
                attributes.RemoveAt(i);
        }
    }

    static void InjectHook(MethodDefinition method, MethodReference hook, bool replace)
    {
        if (replace)
        {
            if (method.ReturnType.MetadataType != MetadataType.Void) throw new InvalidOperationException("replace needs a void method: " + method.FullName);
            method.Body = new MethodBody(method);
            method.Body.GetILProcessor().Emit(OpCodes.Ret);
        }
        ILProcessor il = method.Body.GetILProcessor();
        Instruction first = method.Body.Instructions[0];
        for (int i = 0; i <= method.Parameters.Count; i++)
        {
            Instruction load = il.Create(OpCodes.Ldarg, i == 0 ? method.Body.ThisParameter : method.Parameters[i - 1]);
            il.InsertBefore(first, load);
            if (i > 0 && method.Parameters[i - 1].ParameterType.IsValueType != hook.Parameters[i].ParameterType.IsValueType)
                throw new InvalidOperationException("hook parameter kinds differ for " + method.FullName);
        }
        il.InsertBefore(first, il.Create(OpCodes.Call, hook));
    }

    static void Empty(MethodDefinition m)
    {
        var body = new MethodBody(m);
        ILProcessor il = body.GetILProcessor();
        TypeReference ret = m.ReturnType;
        if (ret.MetadataType != MetadataType.Void)
        {
            var local = new VariableDefinition(ret);
            body.Variables.Add(local);
            body.InitLocals = true;
            il.Emit(OpCodes.Ldloc, local);
        }
        il.Emit(OpCodes.Ret);
        m.Body = body;
    }

    static int Surface(string managedDir, string cutsPath, string entriesPath)
    {
        var cuts = Lines(cutsPath).ToHashSet();
        AssemblyDefinition game = Load(managedDir, "Assembly-CSharp.dll");
        ModuleDefinition module = game.MainModule;
        var allTypes = module.GetTypes().ToList();
        var queue = new Queue<(MethodDefinition method, string via)>();
        var seen = new HashSet<MethodDefinition>();
        var external = new SortedDictionary<string, SortedDictionary<string, string>>();
        var gameMethods = new SortedDictionary<string, string>();
        var seenTypes = new HashSet<TypeDefinition>();

        void Enqueue(MethodDefinition m, string via)
        {
            if (m == null || !seen.Add(m)) return;
            queue.Enqueue((m, via));
        }
        void External(TypeReference type, string member, string via)
        {
            TypeReference element = type.GetElementType();
            if (element.Scope is not AssemblyNameReference a || !Replaced(a.Name)) return;
            string name = element.FullName;
            if (!external.TryGetValue(name, out var members)) external[name] = members = new SortedDictionary<string, string>();
            if (!members.ContainsKey(member)) members[member] = via;
        }
        void Type(TypeReference type, string via)
        {
            if (type == null) return;
            if (type is GenericInstanceType g) foreach (TypeReference arg in g.GenericArguments) Type(arg, via);
            if (type is TypeSpecification spec) { Type(spec.ElementType, via); return; }
            if (type is GenericParameter) return;
            if (type.Scope is AssemblyNameReference a && Replaced(a.Name)) { External(type, "<type>", via); return; }
            TypeDefinition def = type.Resolve();
            if (def == null || def.Module != module || !seenTypes.Add(def)) return;
            Type(def.BaseType, def.FullName);
            foreach (FieldDefinition f in def.Fields) Type(f.FieldType, def.FullName + "." + f.Name);
            Enqueue(def.Methods.FirstOrDefault(x => x.IsConstructor && x.IsStatic), def.FullName + " (type init)");
        }
        void Member(MemberReference member, string via)
        {
            switch (member)
            {
                case MethodReference mr:
                    Type(mr.DeclaringType, via);
                    Type(mr.ReturnType, via);
                    foreach (ParameterDefinition p in mr.Parameters) Type(p.ParameterType, via);
                    if (mr is GenericInstanceMethod gm) foreach (TypeReference arg in gm.GenericArguments) Type(arg, via);
                    if (mr.DeclaringType.GetElementType().Scope is AssemblyNameReference a && Replaced(a.Name))
                    {
                        External(mr.DeclaringType, mr.Name + "(" + string.Join(", ", mr.Parameters.Select(p => p.ParameterType.Name)) + ")"
                            + (mr.HasThis ? "" : " static") + " : " + mr.ReturnType.Name, via);
                        return;
                    }
                    MethodDefinition md = mr.Resolve();
                    if (md == null || md.Module != module) return;
                    Enqueue(md, via);
                    if (md.IsVirtual)
                        foreach (TypeDefinition t in allTypes)
                            foreach (MethodDefinition o in t.Methods)
                                if (o.IsVirtual && o.Name == md.Name && o.Parameters.Count == md.Parameters.Count && Inherits(t, md.DeclaringType))
                                    Enqueue(o, via + " (override)");
                    break;
                case FieldReference fr:
                    Type(fr.DeclaringType, via);
                    Type(fr.FieldType, via);
                    if (fr.DeclaringType.GetElementType().Scope is AssemblyNameReference fa && Replaced(fa.Name))
                        External(fr.DeclaringType, "field " + fr.Name, via);
                    break;
                case TypeReference tr:
                    Type(tr, via);
                    break;
            }
        }

        foreach (string entry in Lines(entriesPath))
        {
            string[] parts = entry.Split("::");
            TypeDefinition type = module.GetType(parts[0]) ?? throw new InvalidOperationException("No type " + parts[0]);
            var methods = type.Methods.Where(m => m.Name == parts[1]).ToList();
            if (methods.Count == 0) throw new InvalidOperationException("No method " + entry);
            Type(type, "entry");
            foreach (MethodDefinition m in methods) Enqueue(m, "entry");
        }
        while (queue.Count > 0)
        {
            var (method, via) = queue.Dequeue();
            string name = method.DeclaringType.FullName + "::" + method.Name;
            bool cut = Matches(cuts, method);
            gameMethods[name + "(" + method.Parameters.Count + ")"] = (cut ? "CUT  " : "") + "via " + via;
            if (cut || !method.HasBody) continue;
            Type(method.DeclaringType, name);
            foreach (VariableDefinition v in method.Body.Variables) Type(v.VariableType, name);
            foreach (Instruction ins in method.Body.Instructions)
                if (ins.Operand is MemberReference mr) Member(mr, name);
        }

        Console.WriteLine("== game methods reached (" + gameMethods.Count + ")");
        foreach (var (m, via) in gameMethods) Console.WriteLine("  " + m + "   <- " + via);
        Console.WriteLine("== engine surface (" + external.Sum(e => e.Value.Count) + " members in " + external.Count + " types)");
        foreach (var (type, members) in external)
        {
            Console.WriteLine(type);
            foreach (var (member, via) in members) Console.WriteLine("    " + member + "   <- " + via);
        }
        return 0;
    }

    static bool Inherits(TypeDefinition t, TypeDefinition baseType)
    {
        for (TypeDefinition? c = t; c != null; c = c.BaseType?.Resolve())
        {
            if (c == baseType) return true;
            if (c.BaseType == null || c.BaseType.Scope is AssemblyNameReference a && Replaced(a.Name)) return false;
        }
        return false;
    }
}
