using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// Unity's Mono JIT (this game does not run in float32 mode) evaluates float arithmetic in double precision and rounds
// only when a value is stored (locals, fields, array elements, arguments, return values). .NET evaluates float
// arithmetic in single precision, so e.g. -0.3f*46f*10f/4.5f gives -30.666666 here and -30.666668 in the game.
// This pass widens every float32 value to float64 as it is pushed on the evaluation stack; ECMA-335 stores to float32
// locations narrow implicitly, so the JIT reproduces Mono's rounding points exactly.
static class MonoFloat
{
    public static int Apply(MethodDefinition method)
    {
        if (!method.HasBody) return 0;
        MethodBody body = method.Body;
        body.SimplifyMacros();
        ILProcessor il = body.GetILProcessor();
        int widened = 0;
        foreach (Instruction ins in body.Instructions.ToList())
        {
            if (ins.OpCode == OpCodes.Box && IsSingle(ins.Operand as TypeReference))
            {
                // box needs a real float32 on the stack.
                il.InsertBefore(ins, il.Create(OpCodes.Conv_R4));
                continue;
            }
            if (!PushesSingle(method, ins)) continue;
            il.InsertAfter(ins, il.Create(OpCodes.Conv_R8));
            widened++;
        }
        body.OptimizeMacros();
        return widened;
    }

    static bool IsSingle(TypeReference t) => t != null && t.MetadataType == MetadataType.Single;

    static bool PushesSingle(MethodDefinition method, Instruction ins)
    {
        OpCode op = ins.OpCode;
        if (op == OpCodes.Ldc_R4 || op == OpCodes.Ldelem_R4 || op == OpCodes.Ldind_R4 || op == OpCodes.Conv_R4) return true;
        if (op == OpCodes.Ldloc) return IsSingle(((VariableDefinition)ins.Operand).VariableType);
        if (op == OpCodes.Ldarg)
        {
            var p = (ParameterDefinition)ins.Operand;
            return p != method.Body.ThisParameter && IsSingle(p.ParameterType);
        }
        if (op == OpCodes.Ldfld || op == OpCodes.Ldsfld) return IsSingle(((FieldReference)ins.Operand).FieldType);
        if (op == OpCodes.Ldelem_Any || op == OpCodes.Ldobj || op == OpCodes.Unbox_Any) return IsSingle(ins.Operand as TypeReference);
        if (op == OpCodes.Call || op == OpCodes.Callvirt)
        {
            var callee = (MethodReference)ins.Operand;
            TypeReference ret = callee.ReturnType;
            if (ret is GenericParameter gp && callee is GenericInstanceMethod gim && gp.Type == GenericParameterType.Method)
                ret = gim.GenericArguments[gp.Position];
            else if (ret is GenericParameter tp && tp.Type == GenericParameterType.Type && callee.DeclaringType is GenericInstanceType git)
                ret = git.GenericArguments[tp.Position];
            return IsSingle(ret);
        }
        return false;
    }
}
