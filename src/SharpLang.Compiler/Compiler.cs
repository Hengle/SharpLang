﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Mono.Cecil;
using SharpLang.CompilerServices.Cecil;
using SharpLLVM;
using AssemblyDefinition = Mono.Cecil.AssemblyDefinition;
using TypeDefinition = Mono.Cecil.TypeDefinition;
using TypeReference = Mono.Cecil.TypeReference;

namespace SharpLang.CompilerServices
{
    /// <summary>
    /// Provides access to C# LLVM code generator.
    /// </summary>
    public sealed partial class Compiler
    {
        /// <summary> Current module being generated. </summary>
        private ModuleRef module;
        private ContextRef context;

        // <summary> Builder used for method codegen. <summary>
        private BuilderRef builder;
        // <summary> Extra builder used for method codegen. <summary>
        private BuilderRef builder2;

        private DIBuilderRef debugBuilder;
        private TargetDataRef targetData;

        /// <summary> Currently compiled assembly. </summary>
        private AssemblyDefinition assembly;
        
        /// <summary> Corlib assembly. </summary>
        private AssemblyDefinition corlib;

        /// <summary> Cecil TypeReference to generated SharpLang Type mapping. </summary>
        private Dictionary<TypeReference, Type> types = new Dictionary<TypeReference, Type>(MemberEqualityComparer.Default);

        /// <summary> Cecil methodReference to generated SharpLang Function mapping. </summary>
        private Dictionary<MethodReference, Function> functions = new Dictionary<MethodReference, Function>(MemberEqualityComparer.Default);

        /// <summary> List of classes that still need to be generated. </summary>
        private Queue<Type> classesToGenerate = new Queue<Type>();

        /// <summary> List of methods that still need to be generated. </summary>
        private Queue<KeyValuePair<MethodReference, Function>> methodsToCompile = new Queue<KeyValuePair<MethodReference, Function>>();

        private Dictionary<Mono.Cecil.ModuleDefinition, ValueRef> metadataPerModule;

        /// <summary> True when running unit tests. This will try to avoid using real mscorlib for faster codegen, linking and testing. </summary>
        public bool TestMode { get; set; }

        public void PrepareAssembly(AssemblyDefinition assembly)
        {
            this.assembly = assembly;

            RegisterExternalTypes();

            // Resolve corlib assembly
            corlib = assembly.MainModule.Import(typeof (void)).Resolve().Module.Assembly;

            // Prepare LLVM context, module and data layouts
            context = LLVM.GetGlobalContext();
            module = LLVM.ModuleCreateWithName(assembly.Name.Name);

            // TODO: Choose appropriate triple depending on target
            LLVM.SetTarget(module, "i686-pc-mingw32");

            // Prepare system types, for easier access
            InitializeCommonTypes();

            // Prepare LLVM builders
            builder = LLVM.CreateBuilderInContext(context);
            builder2 = LLVM.CreateBuilderInContext(context);
            debugBuilder = LLVM.DIBuilderCreate(module);

            if (!TestMode)
            {
                // Register SharpLangModule objects for each module
                metadataPerModule = new Dictionary<Mono.Cecil.ModuleDefinition, ValueRef>();
                var mangledModuleName = Regex.Replace(assembly.Name.Name + ".sharplangmodule", @"(\W)", "_");
                var sharpLangModuleGlobal = LLVM.AddGlobal(module, sharpLangModuleType.ObjectTypeLLVM, mangledModuleName);
                metadataPerModule[assembly.MainModule] = sharpLangModuleGlobal;

                // Generate extern globals for SharpLangModule instances of other modules
                foreach (var referencedAssembly in referencedAssemblies)
                {
                    mangledModuleName = Regex.Replace(referencedAssembly.Name.Name + ".sharplangmodule", @"(\W)", "_");
                    var externalSharpLangModuleGlobal = LLVM.AddGlobal(module, sharpLangModuleType.ObjectTypeLLVM, mangledModuleName);
                    LLVM.SetLinkage(externalSharpLangModuleGlobal, Linkage.ExternalLinkage);
                    metadataPerModule[referencedAssembly.MainModule] = externalSharpLangModuleGlobal;
                }
            }
        }

        public byte[] ReadMetadata(string fileName)
        {
            using (var stream = File.OpenRead(fileName))
            {
                var peHeader = new PEHeaders(stream);

                var metadata = new byte[peHeader.MetadataSize];
                stream.Position = peHeader.MetadataStartOffset;
                stream.Read(metadata, 0, peHeader.MetadataSize);

                return metadata;
            }
        }

        public void RegisterType(TypeReference typeReference)
        {
            var type = GetType(typeReference, TypeState.TypeComplete);
            EmitType(type, true);
        }

        public void ProcessAssembly(AssemblyDefinition assembly)
        {
            // Transfom all types in this assembly into SharpLang types.
            foreach (var assemblyModule in assembly.Modules)
            {
                var typeReferences = assemblyModule.GetTypeReferences();
                foreach (var type in typeReferences)
                {
                    GetType(type, TypeState.TypeComplete);
                }

                var memberReferences = assemblyModule.GetMemberReferences();
                foreach (var member in memberReferences)
                {
                    var method = member as MethodReference;
                    if (member.DeclaringType.ContainsGenericParameter)
                        continue;
                    GetType(member.DeclaringType, TypeState.TypeComplete);
                    if (method != null)
                    {
                        if (!method.HasGenericParameters)
                            CreateFunction(method);
                    }
                }

                foreach (var type in assemblyModule.Types)
                {
                    if (!type.HasGenericParameters && type.FullName != typeof(void).FullName)
                        GetClass(type);

                    foreach (var nestedType in type.NestedTypes)
                    {
                        if (!nestedType.HasGenericParameters)
                            GetClass(nestedType);
                    }
                }
            }
        }

        public ModuleRef GenerateModule()
        {
            LLVM.DIBuilderCreateCompileUnit(debugBuilder,
                0x4, // DW_LANG_C_plus_plus
                "file", "directory", "SharpLang", false, string.Empty, 1, string.Empty);

            LLVM.AddModuleFlag(module, "Dwarf Version", 4);
            LLVM.AddModuleFlag(module, "Debug Info Version", 1);

            // Process methods
            while (classesToGenerate.Count > 0)
            {
                var classToGenerate = classesToGenerate.Dequeue();
                if (classToGenerate.IsLocal)
                {
                    PrepareClassMethods(classToGenerate);
                }
            }

            // Generate code
            while (methodsToCompile.Count > 0)
            {
                var methodToCompile = methodsToCompile.Dequeue();
                //Console.WriteLine("Compiling {0}", methodToCompile.Key.FullName);
                CompileFunction(methodToCompile.Key, methodToCompile.Value);
            }

            // Prepare global module constructor
            var globalCtorFunctionType = LLVM.FunctionType(LLVM.VoidTypeInContext(context), new TypeRef[0], false);
            var globalCtor = LLVM.AddFunction(module, "initializeSharpLangModule", globalCtorFunctionType);
            LLVM.SetLinkage(globalCtor, Linkage.PrivateLinkage);
            LLVM.PositionBuilderAtEnd(builder, LLVM.AppendBasicBlockInContext(context, globalCtor, string.Empty));

            if (!TestMode)
            {
                // Emit metadata
                var metadataBytes = ReadMetadata(assembly.MainModule.FullyQualifiedName);
                var metadataData = CreateDataConstant(metadataBytes);

                var metadataGlobal = LLVM.AddGlobal(module, LLVM.TypeOf(metadataData), "metadata");
                LLVM.SetInitializer(metadataGlobal, metadataData);
                LLVM.SetLinkage(metadataGlobal, Linkage.PrivateLinkage);


                // Use metadata to initialize a SharpLangModule, that will be created at module load time using a global ctor
                sharpLangModuleType = GetType(corlib.MainModule.GetType("System.SharpLangModule"), TypeState.VTableEmitted);
                sharpLangTypeType = GetType(corlib.MainModule.GetType("System.SharpLangTypeDefinition"), TypeState.VTableEmitted); // Was only StackComplete until now

                // Get ctor for SharpLangModule and SharpLangType
                var moduleCtor = sharpLangModuleType.Class.Functions.First(x => x.DeclaringType == sharpLangModuleType && x.MethodReference.Resolve().IsConstructor);

                var registerTypeMethod = sharpLangModuleType.Class.Functions.First(x => x.DeclaringType == sharpLangModuleType && x.MethodReference.Name == "RegisterType");
                var sortTypesMethod = sharpLangModuleType.Class.Functions.First(x => x.DeclaringType == sharpLangModuleType && x.MethodReference.Name == "SortTypes");

                // Initialize SharpLangModule instance:
                //   new SharpLangModule(moduleName, metadataStart, metadataLength)
                var sharpLangModuleGlobal = metadataPerModule[assembly.MainModule];
                var functionContext = new FunctionCompilerContext(globalCtor);
                functionContext.Stack = new List<StackValue>();
                functionContext.Stack.Add(new StackValue(StackValueType.Object, sharpLangModuleType, sharpLangModuleGlobal));
                functionContext.Stack.Add(new StackValue(StackValueType.NativeInt, intPtr, metadataGlobal));
                functionContext.Stack.Add(new StackValue(StackValueType.Int32, int32, LLVM.ConstInt(int32LLVM, (ulong)metadataBytes.Length, false)));

                // Setup initial value (note: VTable should be valid)
                LLVM.SetLinkage(sharpLangModuleGlobal, Linkage.ExternalLinkage);
                LLVM.SetInitializer(sharpLangModuleGlobal, SetupVTableConstant(LLVM.ConstNull(sharpLangModuleType.ObjectTypeLLVM), sharpLangModuleType.Class));
                metadataPerModule[assembly.MainModule] = sharpLangModuleGlobal;

                EmitCall(functionContext, moduleCtor.Signature, moduleCtor.GeneratedValue);

                // Register types
                foreach (var type in types)
                {
                    var @class = type.Value.Class;

                    // Skip incomplete types
                    if (@class == null || !@class.IsEmitted)
                        continue;

                    // Skip if no RTTI initializer
                    if (LLVM.GetInitializer(@class.GeneratedEETypeRuntimeLLVM) == ValueRef.Empty)
                        continue;

                    // Skip if interface (fake RTTI pointer)
                    if (type.Value.TypeDefinitionCecil.IsInterface)
                        continue;

                    functionContext.Stack.Add(new StackValue(StackValueType.NativeInt, intPtr, LLVM.ConstPointerCast(@class.GeneratedEETypeRuntimeLLVM, intPtrLLVM)));
                    EmitCall(functionContext, registerTypeMethod.Signature, registerTypeMethod.GeneratedValue);
                }

                // Sort and remove duplicates after adding all our types
                // TODO: Somehow sort everything before insertion at compile time?
                EmitCall(functionContext, sortTypesMethod.Signature, sortTypesMethod.GeneratedValue);

                LLVM.BuildRetVoid(builder);
            }
            else
            {
                LLVM.BuildRetVoid(builder);
            }

            // Prepare global ctors
            {
                var globalCtorType = LLVM.StructTypeInContext(context, new[] { int32LLVM, LLVM.PointerType(globalCtorFunctionType, 0) }, true);
                var globalCtorsGlobal = LLVM.AddGlobal(module, LLVM.ArrayType(globalCtorType, 1), "llvm.global_ctors");
                LLVM.SetLinkage(globalCtorsGlobal, Linkage.AppendingLinkage);
                LLVM.SetInitializer(globalCtorsGlobal,
                    LLVM.ConstArray(globalCtorType, new [] {
                        LLVM.ConstNamedStruct(globalCtorType, new[]
                        {
                            LLVM.ConstInt(int32LLVM, (ulong)65536, false),
                            globalCtor,
                        })}));
            }

            // Emit "main" which will call the assembly entry point (if any)
            Function entryPoint;
	        if (assembly.EntryPoint != null && functions.TryGetValue(assembly.EntryPoint, out entryPoint))
	        {
                var mainFunctionType = LLVM.FunctionType(int32LLVM, new TypeRef[0], false);
	            var mainFunction = LLVM.AddFunction(module, "main", mainFunctionType);
                LLVM.SetLinkage(mainFunction, Linkage.ExternalLinkage);
                LLVM.PositionBuilderAtEnd(builder, LLVM.AppendBasicBlockInContext(context, mainFunction, string.Empty));

	            var parameters = (entryPoint.ParameterTypes.Length > 0)
	                ? new[] { LLVM.ConstPointerNull(entryPoint.ParameterTypes[0].DefaultTypeLLVM) }
	                : new ValueRef[0];

                LLVM.BuildCall(builder, entryPoint.GeneratedValue, parameters, string.Empty);
                LLVM.BuildRet(builder, LLVM.ConstInt(int32LLVM, 0, false));
	        }

            LLVM.DIBuilderFinalize(debugBuilder);
            LLVM.DIBuilderDispose(debugBuilder);
            LLVM.DisposeBuilder(builder);

            return module;
        }

        TypeReference GetBaseTypeDefinition(TypeReference typeReference)
        {
            if (typeReference is ArrayType)
            {
                // Return ArrayType
                return corlib.MainModule.GetType(typeof(Array).FullName);
            }

            // Default: resolve to get real type
            return typeReference.Resolve().BaseType;
        }

        /// <summary>
        /// Gets the type definition containing all the methods for the given type.
        /// </summary>
        /// <returns></returns>
        TypeDefinition GetMethodTypeDefinition(TypeReference typeReference)
        {
            if (typeReference is ArrayType)
            {
                // Return ArrayType
                return corlib.MainModule.GetType(typeof(Array).FullName);
            }

            // Default: resolve to get real type
            return typeReference.Resolve();
        }

        void PrepareClassMethods(Type type)
        {
            var @class = GetClass(type);

            // Already processed?
            if (@class == null || @class.MethodCompiled)
                return;

            @class.MethodCompiled = true;

            // Array: no need to do anything (its type definition, Array, has already been processed)
            if (@class.Type.TypeReferenceCecil is ArrayType)
                return;

            var typeDefinition = GetMethodTypeDefinition(@class.Type.TypeReferenceCecil);

            bool isInterface = typeDefinition.IsInterface;

            // Process methods, Virtual first, then non virtual, then static
            foreach (var method in typeDefinition.Methods.OrderBy(x => x.IsVirtual ? 0 : (!x.IsStatic ? 1 : 2)))
            {
                var methodReference = ResolveGenericMethod(@class.Type.TypeReferenceCecil, method);

                // If a method contains generic parameters, skip it
                // Its closed instantiations (with generic arguments) is what needs to be generated.
                // (except interface methods)
                // Using ResolveGenericsVisitor.ContainsGenericParameters because Cecil one's doesn't seem to match what .NET Type does.
                // TODO: Might need a more robust generic resolver/analyzer system soon.
                if (ResolveGenericsVisitor.ContainsGenericParameters(methodReference))
                    continue;

                var function = CreateFunction(methodReference);

                @class.Functions.Add(function);

                if (method.IsSpecialName && method.Name == ".cctor")
                {
                    @class.StaticCtor = function;
                }

                if (method.IsVirtual)
                {
                    if (isInterface)
                    {
                        // Store IMT slot, and unique IMT key (generated using global pointer)
                        function.VirtualSlot = (int)(GetMethodId(methodReference) % InterfaceMethodTableSize);
                        function.InterfaceSlot = function.GeneratedValue;
                    }
                    else if (method.IsNewSlot)
                    {
                        // New slot
                        function.VirtualSlot = @class.VirtualTable.Count;
                        @class.VirtualTable.Add(function);
                    }
                    else
                    {
                        // Find slot in base types
                        var baseType = @class.BaseType;
                        Function matchedMethod = null;
                        while (baseType != null)
                        {
                            matchedMethod = CecilExtensions.TryMatchMethod(baseType, methodReference);
                            if (matchedMethod != null)
                                break;
                            baseType = baseType.BaseType;
                        }

                        if (matchedMethod == null)
                            throw new InvalidOperationException(string.Format("Could not find a slot for virtual function {0} in parents of class {1}", method, @class.Type.TypeReferenceCecil));

                        function.VirtualSlot = matchedMethod.VirtualSlot;
                        @class.VirtualTable[function.VirtualSlot] = function;
                    }
                }
                else
                {
                    // New slot
                    function.VirtualSlot = @class.VirtualTable.Count;
                    @class.VirtualTable.Add(function);
                }
            }
        }

        private static MethodReference ResolveGenericMethod(TypeReference typeReference, MethodReference method)
        {
            var genericInstanceType = typeReference as GenericInstanceType;
            if (genericInstanceType != null)
                return method.MakeGeneric(genericInstanceType.GenericArguments.ToArray());

            return method;
        }
    }
}