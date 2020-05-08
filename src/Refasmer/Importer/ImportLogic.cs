using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace JetBrains.Refasmer
{
    public partial class MetadataImporter
    {
        private TypeDefinitionHandle ImportTypeDefinitionSkeleton( TypeDefinitionHandle srcHandle )
        {
            var src = _reader.GetTypeDefinition(srcHandle);
            
            var dstHandle = _builder.AddTypeDefinition(src.Attributes, ImportValue(src.Namespace), ImportValue(src.Name),
                Import(src.BaseType), NextFieldHandle(), NextMethodHandle());

            Trace?.Invoke($"Imported {ToString(srcHandle)} -> {RowId(dstHandle):X}");

            using var _ = WithLogPrefix($"[{ToString(src)}]");

            foreach (var srcFieldHandle in src.GetFields())
            {
                var srcField = _reader.GetFieldDefinition(srcFieldHandle);
                
                if (Filter?.AllowImport(srcField, _reader) == false)
                    continue;

                var dstFieldHandle = _builder.AddFieldDefinition(srcField.Attributes, ImportValue(srcField.Name),
                    ImportSignatureWithHeader(srcField.Signature));
                _fieldDefinitionCache.Add(srcFieldHandle, dstFieldHandle);
                Trace?.Invoke($"Imported {ToString(srcFieldHandle)} -> {RowId(dstFieldHandle):X}");
            }

            foreach (var srcMethodHandle in src.GetMethods())
            {
                var srcMethod = _reader.GetMethodDefinition(srcMethodHandle);

                if (Filter?.AllowImport(srcMethod, _reader) == false)
                    continue;

                var dstMethodHandle = _builder.AddMethodDefinition(srcMethod.Attributes, srcMethod.ImplAttributes,
                    ImportValue(srcMethod.Name),
                    ImportSignatureWithHeader(srcMethod.Signature), -1, NextParameterHandle());
                _methodDefinitionCache.Add(srcMethodHandle, dstMethodHandle);
                Trace?.Invoke($"Imported {ToString(srcMethodHandle)} -> {RowId(dstMethodHandle):X}");

                using var __ = WithLogPrefix($"[{ToString(srcMethodHandle)}]");
                foreach (var srcParameterHandle in srcMethod.GetParameters())
                {
                    var srcParameter = _reader.GetParameter(srcParameterHandle);
                    var dstParameterHandle = _builder.AddParameter(srcParameter.Attributes,
                        ImportValue(srcParameter.Name), srcParameter.SequenceNumber);
                    _parameterCache.Add(srcParameterHandle, dstParameterHandle);
                    Trace?.Invoke($"Imported {ToString(srcParameterHandle)} -> {RowId(dstParameterHandle):X}");

                    var defaultValue = srcParameter.GetDefaultValue();

                    if (!defaultValue.IsNil)
                        ImportDefaultValue(defaultValue, dstParameterHandle);
                }
            }

            return dstHandle;
        }

         private void ImportTypeDefinitionAccessories( TypeDefinitionHandle srcHandle, TypeDefinitionHandle dstHandle)
        {
            var src = _reader.GetTypeDefinition(srcHandle);
        
            using var _ = WithLogPrefix($"[{ToString(src)}]");

            foreach (var srcInterfaceImplHandle in src.GetInterfaceImplementations())
            {
                var srcInterfaceImpl = _reader.GetInterfaceImplementation(srcInterfaceImplHandle);
                var dstInterfaceHandle = Import(srcInterfaceImpl.Interface);

                var dstInterfaceImplHandle = _builder.AddInterfaceImplementation(dstHandle, dstInterfaceHandle);
                _interfaceImplementationCache.Add(srcInterfaceImplHandle, dstInterfaceImplHandle);
                Trace?.Invoke(
                    $"Imported interface implementation {ToString(srcInterfaceImplHandle)} ->  {RowId(dstInterfaceHandle):X} {RowId(dstInterfaceImplHandle):X}");
            }

            foreach (var srcMethodImplementationHandle in src.GetMethodImplementations())
                ImportEntity(srcMethodImplementationHandle, _methodImplementationCache, _reader.GetMethodImplementation,
                    srcImpl =>
                    {
                        var body = Import(srcImpl.MethodBody);
                        var decl = Import(srcImpl.MethodDeclaration);

                        return body.IsNil || decl.IsNil
                            ? default
                            : _builder.AddMethodImplementation(dstHandle, body, decl);
                    },
                    ToString, IsNil);

            if (src.GetEvents().Any())
            {
                _builder.AddEventMap(dstHandle, NextEventHandle());
                foreach (var eventHandle in src.GetEvents())
                    ImportEvent(eventHandle);
            }

            if (src.GetProperties().Any())
            {
                _builder.AddPropertyMap(dstHandle, NextPropertyHandle());
                foreach (var propertyHandle in src.GetProperties())
                    ImportProperty(propertyHandle);
            }

            if (!src.GetLayout().IsDefault)
            {
                _builder.AddTypeLayout(dstHandle, (ushort) src.GetLayout().PackingSize, (uint) src.GetLayout().Size);
                Trace?.Invoke($"Imported layout Size={src.GetLayout().Size} PackingSize={src.GetLayout().PackingSize}");
            }
        }

        private void ImportFieldDefinitionAccessories( FieldDefinitionHandle srcHandle, FieldDefinitionHandle dstHandle )
        {
            var src = _reader.GetFieldDefinition(srcHandle);

            using var _ = WithLogPrefix($"[{ToString(src)}]");

            if (!src.GetDefaultValue().IsNil)
            {
                var srcConst = _reader.GetConstant(src.GetDefaultValue());
                var value = _reader.GetBlobReader(srcConst.Value).ReadConstant(srcConst.TypeCode);

                var dstConst = _builder.AddConstant(dstHandle, value);

                Trace?.Invoke($"Imported default value {ToString(srcConst)} -> {RowId(dstConst):X} = {value}");
            }

            if (!src.GetMarshallingDescriptor().IsNil)
            {
                _builder.AddMarshallingDescriptor(dstHandle, ImportValue(src.GetMarshallingDescriptor()));
                Trace?.Invoke($"Imported marshalling descriptor {ToString(src.GetMarshallingDescriptor())}");
            }

            if (src.GetOffset() != -1)
            {
                _builder.AddFieldLayout(dstHandle, src.GetOffset());
                Trace?.Invoke($"Imported offset {src.GetOffset()}");
            }

            if (src.GetRelativeVirtualAddress() != 0)
            {
                _builder.AddFieldRelativeVirtualAddress(dstHandle, src.GetRelativeVirtualAddress());
                Trace?.Invoke($"Imported relative virtual address {src.GetRelativeVirtualAddress()}");
            }

        }

        private void ImportMethodDefinitionAccessories( MethodDefinitionHandle srcHandle, MethodDefinitionHandle dstHandle )
        {
            var src = _reader.GetMethodDefinition(srcHandle);
            using var _ = WithLogPrefix($"[{ToString(src)}]");

            var srcImport = src.GetImport();

            if (!srcImport.Name.IsNil)
            {
                _builder.AddMethodImport(dstHandle, srcImport.Attributes, ImportValue(srcImport.Name),
                    Import(srcImport.Module));
                Trace?.Invoke($"Imported method import {ToString(srcImport.Module)} {ToString(srcImport.Name)}");
            }
        }

        private void ImportEvent( EventDefinitionHandle srcHandle )
        {
            var src = _reader.GetEventDefinition(srcHandle);

            var accessors = src.GetAccessors();

            var adder = Import(accessors.Adder);
            var remover = Import(accessors.Remover);
            var raiser = Import(accessors.Raiser);

            var others = accessors.Others
                .Select(a => Tuple.Create(a, Import(a)))
                .Where(a => !a.Item2.IsNil)
                .ToList();

            if (adder.IsNil && remover.IsNil && raiser.IsNil && !others.Any())
            {
                Trace?.Invoke($"Not imported event {ToString(src)}");
                return;
            }

            var dstHandle = _builder.AddEvent(src.Attributes, ImportValue(src.Name), Import(src.Type));
            _eventDefinitionCache.Add(srcHandle, dstHandle);
            Trace?.Invoke($"Imported event {ToString(src)} -> {RowId(dstHandle):X}");

            using var _ = WithLogPrefix($"[{ToString(src)}]");

            if (!adder.IsNil)
            {
                _builder.AddMethodSemantics(dstHandle, MethodSemanticsAttributes.Adder, adder);
                Trace?.Invoke($"Imported adder {ToString(accessors.Adder)} -> {RowId(adder):X}");
            }

            if (!remover.IsNil)
            {
                _builder.AddMethodSemantics(dstHandle, MethodSemanticsAttributes.Remover, remover);
                Trace?.Invoke($"Imported remover {ToString(accessors.Remover)} -> {RowId(remover):X}");
            }

            if (!raiser.IsNil)
            {
                _builder.AddMethodSemantics(dstHandle, MethodSemanticsAttributes.Raiser, raiser);
                Trace?.Invoke($"Imported raiser {ToString(accessors.Raiser)} -> {RowId(raiser):X}");
            }

            foreach (var (srcAccessor, dstAccessor) in others)
            {
                _builder.AddMethodSemantics(dstHandle, MethodSemanticsAttributes.Other, dstAccessor);
                Trace?.Invoke($"Imported other {ToString(srcAccessor)} -> {RowId(dstAccessor):X}");
            }

        }

        private void ImportProperty( PropertyDefinitionHandle srcHandle )
        {
            var src = _reader.GetPropertyDefinition(srcHandle);

            var accessors = src.GetAccessors();

            var getter = Import(accessors.Getter);
            var setter = Import(accessors.Setter);

            var others = accessors.Others
                .Select(a => Tuple.Create(a, Import(a)))
                .Where(a => !a.Item2.IsNil)
                .ToList();

            if (getter.IsNil && setter.IsNil && !others.Any())
            {
                Trace?.Invoke($"Not imported property {ToString(src)}");
                return;
            }

            var dstHandle = _builder.AddProperty(src.Attributes, ImportValue(src.Name), ImportSignatureWithHeader(src.Signature));
            _propertyDefinitionCache.Add(srcHandle, dstHandle);

            Trace?.Invoke($"Imported property {ToString(src)} -> {RowId(dstHandle):X}");

            using var _ = WithLogPrefix($"[{ToString(src)}]");

            if (!getter.IsNil)
            {
                _builder.AddMethodSemantics(dstHandle, MethodSemanticsAttributes.Getter, getter);
                Trace?.Invoke($"Imported getter {ToString(accessors.Getter)} -> {RowId(getter):X}");
            }

            if (!setter.IsNil)
            {
                _builder.AddMethodSemantics(dstHandle, MethodSemanticsAttributes.Setter, setter);
                Trace?.Invoke($"Imported setter {ToString(accessors.Setter)} -> {RowId(setter):X}");
            }

            foreach (var (srcAccessor, dstAccessor) in others)
            {
                _builder.AddMethodSemantics(dstHandle, MethodSemanticsAttributes.Other, dstAccessor);
                Trace?.Invoke($"Imported other {ToString(srcAccessor)} -> {RowId(dstAccessor):X}");
            }

            var defaultValue = src.GetDefaultValue();
            if (!defaultValue.IsNil)
                ImportDefaultValue(defaultValue, dstHandle);
        }

        private void ImportGenericConstraints( EntityHandle entityHandle, GenericParameterHandleCollection srcParams )
        {
            var srcConstraints = new List<Tuple<GenericParameterHandle, GenericParameterConstraintHandle>>();

            foreach (var srcParamHandle in srcParams)
            {
                var srcParam = _reader.GetGenericParameter(srcParamHandle);
                var dstParamHandle = _builder.AddGenericParameter(entityHandle, srcParam.Attributes,
                    ImportValue(srcParam.Name), srcParam.Index);
                _genericParameterCache.Add(srcParamHandle, dstParamHandle);
                srcConstraints.AddRange(srcParam.GetConstraints().Select(x => Tuple.Create(dstParamHandle, x)));

                Trace?.Invoke($"Imported generic parameter {ToString(srcParam)} -> {RowId(dstParamHandle):X}");
            }

            foreach (var (dstParam, srcConstraintHandle) in srcConstraints)
                ImportEntity(srcConstraintHandle, _genericParameterConstraintCache,
                    _reader.GetGenericParameterConstraint,
                    src => _builder.AddGenericParameterConstraint(dstParam, Import(src.Type)),
                    ToString, IsNil);
        }
 
        private void ImportDefaultValue( ConstantHandle defaultValue, EntityHandle dstHandle )
        {
            if (!defaultValue.IsNil)
            {
                var srcConst = _reader.GetConstant(defaultValue);
                var value = _reader.GetBlobReader(srcConst.Value).ReadConstant(srcConst.TypeCode);
                var dstConst = _builder.AddConstant(dstHandle, value);

                Trace?.Invoke($"Imported default value {ToString(srcConst)} -> {RowId(dstConst):X} = {value}");
            }
        }

        public bool IsInternalsVisible()
        {
            var internalsVisibleTo = _reader.GetAssemblyDefinition().GetCustomAttributes()
                .Select(_reader.GetCustomAttribute)
                .Where(attr =>
                {
                    EntityHandle attrClassHandle;
                    switch (attr.Constructor.Kind)
                    {
                        case HandleKind.MemberReference:
                            attrClassHandle = _reader.GetMemberReference((MemberReferenceHandle) attr.Constructor).Parent;
                            break;
                        case HandleKind.MethodDefinition:
                            attrClassHandle = _reader.GetMethodDefinition((MethodDefinitionHandle) attr.Constructor).GetDeclaringType();
                            break;
                        default:
                            return false;
                    }

                    string attrClassName;

                    switch (attrClassHandle.Kind)
                    {
                        case HandleKind.TypeDefinition:
                            var typeDef = _reader.GetTypeDefinition((TypeDefinitionHandle) attrClassHandle);
                            attrClassName = $"{_reader.GetString(typeDef.Namespace)}.{_reader.GetString(typeDef.Name)}";
                            break;
                        case HandleKind.TypeReference:
                            var typeRef = _reader.GetTypeReference((TypeReferenceHandle) attrClassHandle);
                            attrClassName = $"{_reader.GetString(typeRef.Namespace)}.{_reader.GetString(typeRef.Name)}";
                            break;
                        default:
                            return false;
                    }

                    return attrClassName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute";
                }).ToList();
            
            return internalsVisibleTo.Any();
        }
        
        public void Import()
        {
            var srcAssembly = _reader.GetAssemblyDefinition();

            _builder.AddAssembly(ImportValue(srcAssembly.Name), srcAssembly.Version, ImportValue(srcAssembly.Culture),
                ImportValue(srcAssembly.PublicKey),
                srcAssembly.Flags, srcAssembly.HashAlgorithm);
            Debug?.Invoke($"Imported assembly {ToString(srcAssembly)}");


            var srcModule = _reader.GetModuleDefinition();
            
            _builder.AddModule(srcModule.Generation, ImportValue(srcModule.Name), ImportValue(srcModule.Mvid),
                ImportValue(srcModule.GenerationId),
                ImportValue(srcModule.BaseGenerationId));
            Debug?.Invoke($"Imported module {ToString(srcModule)}");

            Debug?.Invoke($"Importing assembly files");
            foreach (var srcHandle in _reader.AssemblyFiles)
                Import(srcHandle);

            var index = 1;
            Debug?.Invoke($"Preparing type list for import");
            foreach (var srcHandle in _reader.TypeDefinitions)
                if (Filter?.AllowImport(_reader.GetTypeDefinition(srcHandle), _reader) != false)
                    _typeDefinitionCache[srcHandle] = MetadataTokens.TypeDefinitionHandle(index++);
            
            Debug?.Invoke($"Importing type definitions");
            foreach (var srcHandle in _reader.TypeDefinitions.Where(_typeDefinitionCache.ContainsKey))
            {
                var dstHandle = ImportTypeDefinitionSkeleton(srcHandle);
                if (dstHandle != _typeDefinitionCache[srcHandle])
                    throw new Exception("WTF: type handle mismatch");
            }

            foreach (var (srcHandle, dstHandle) in _typeDefinitionCache)
                ImportTypeDefinitionAccessories(srcHandle, dstHandle);

            Debug?.Invoke($"Importing method definitions");
            foreach (var (srcHandle, dstHandle) in _methodDefinitionCache)
                ImportMethodDefinitionAccessories(srcHandle, dstHandle);

            Debug?.Invoke($"Importing field definitions");
            foreach (var (srcHandle, dstHandle) in _fieldDefinitionCache)
                ImportFieldDefinitionAccessories(srcHandle, dstHandle);

            Debug?.Invoke($"Importing nested classes");
            var nestedTypes = _typeDefinitionCache
                .Select(kv => Tuple.Create(kv.Value, _reader.GetTypeDefinition(kv.Key).GetNestedTypes()))
                .SelectMany(x => x.Item2.Select(y => Tuple.Create(x.Item1, y, Import(y))))
                .Where(x => !x.Item3.IsNil)
                .OrderBy(x => RowId(x.Item3))
                .ToList();

            foreach (var (dstHandle, srcNested, dstNested) in nestedTypes)
            {
                _builder.AddNestedType(dstNested, dstHandle);
                Trace?.Invoke($"Imported nested type {ToString(srcNested)} -> {RowId(dstNested):X}");
            }

            
            var generic = _typeDefinitionCache
                .Select(kv => Tuple.Create((EntityHandle) kv.Value, _reader.GetTypeDefinition(kv.Key).GetGenericParameters()))
                .Concat(_methodDefinitionCache
                    .Select(kv => Tuple.Create((EntityHandle) kv.Value, _reader.GetMethodDefinition(kv.Key).GetGenericParameters())))
                .OrderBy(x => CodedIndex.TypeOrMethodDef(x.Item1))
                .ToList();

            Debug?.Invoke($"Importing generic constraints");
            foreach (var (dstHandle, genericParams) in generic)
                ImportGenericConstraints(dstHandle, genericParams);

            
            Debug?.Invoke($"Importing custom attributes");
            foreach (var src in _reader.CustomAttributes)
                Import(src);

            Debug?.Invoke($"Importing declarative security attributes");
            foreach (var src in _reader.DeclarativeSecurityAttributes)
                Import(src);

            Debug?.Invoke($"Importing exported types");
            foreach (var src in _reader.ExportedTypes)
                Import(src);

            Debug?.Invoke($"Importing done");
        }
        
    }
}