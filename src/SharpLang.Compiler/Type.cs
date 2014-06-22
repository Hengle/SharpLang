using System;
using Mono.Cecil;
using SharpLLVM;

namespace SharpLang.CompilerServices
{
    /// <summary>
    /// Describes a type (could be a <see cref="Class"/>, a delegate, a pointer to a <see cref="Type"/>, etc...).
    /// </summary>
    class Type
    {
        internal Class Class;
        internal bool IsLocal;

        public Type(TypeReference typeReference, TypeRef dataType, TypeRef objectType, StackValueType stackType)
        {
            TypeReference = typeReference;
            DataType = dataType;
            ObjectType = objectType;
            StackType = stackType;
            DefaultType = (stackType == StackValueType.Object || stackType == StackValueType.Reference) ? LLVM.PointerType(ObjectType, 0) : DataType;
            StackType = stackType;

            switch (stackType)
            {
                case StackValueType.NativeInt:
                    TypeOnStack = LLVM.PointerType(LLVM.Int8TypeInContext(LLVM.GetTypeContext(dataType)), 0);
                    break;
                case StackValueType.Float:
                    TypeOnStack = LLVM.DoubleTypeInContext(LLVM.GetTypeContext(dataType));
                    break;
                case StackValueType.Int32:
                    TypeOnStack = LLVM.Int32TypeInContext(LLVM.GetTypeContext(dataType));
                    break;
                case StackValueType.Int64:
                    TypeOnStack = LLVM.Int64TypeInContext(LLVM.GetTypeContext(dataType));
                    break;
                case StackValueType.Value:
                case StackValueType.Object:
                case StackValueType.Reference:
                    TypeOnStack = DefaultType;
                    break;
            }
        }

        /// <summary>
        /// Gets the LLVM default type.
        /// </summary>
        /// <value>
        /// The LLVM default type.
        /// </value>
        public TypeRef DefaultType { get; private set; }

        /// <summary>
        /// Gets the LLVM object type (object header and <see cref="DataType"/>).
        /// </summary>
        /// <value>
        /// The LLVM boxed type (object header and <see cref="DataType"/>).
        /// </value>
        public TypeRef ObjectType { get; private set; }

        /// <summary>
        /// Gets the LLVM data type.
        /// </summary>
        /// <value>
        /// The LLVM data type (fields).
        /// </value>
        public TypeRef DataType { get; private set; }

        /// <summary>
        /// Gets the LLVM type when on the stack.
        /// </summary>
        /// <value>
        /// The LLVM type when on the stack.
        /// </value>
        public TypeRef TypeOnStack { get; private set; }

        public TypeReference TypeReference { get; private set; }

        public StackValueType StackType { get; private set; }

        /// <inheritdoc/>
        public override string ToString()
        {
            return TypeReference.ToString();
        }
    }
}