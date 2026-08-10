using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Runner.Services;

internal static class ManagedPeSecurityPolicy
{
    internal static string? Validate(byte[] peImage)
    {
        try
        {
            using var stream = new MemoryStream(peImage, writable: false);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!peReader.HasMetadata || peReader.PEHeaders.CorHeader is null)
            {
                return "Security policy: compiler output is not a valid managed assembly.";
            }

            var metadata = peReader.GetMetadataReader();

            // Do not apply the source-level framework API denylist to every TypeRef/MemberRef
            // in the emitted PE. Roslyn legitimately synthesizes framework references that do
            // not exist in the student's source (async/iterator state machines, records, array
            // initializers and debugger metadata are common examples). Treating all emitted
            // references as user intent makes ordinary C# language features fail closed.
            //
            // User-selected framework APIs are already checked semantically before emit by
            // RoslynSecurityPolicy. This PE pass is the independent structural backstop: it
            // rejects native/imported code and early-execution/interoperability attributes that
            // must never survive into a submission assembly.

            foreach (var handle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(handle);
                if ((type.Attributes & TypeAttributes.Import) != 0)
                {
                    return "Security policy: imported native types are not available in the OJ.";
                }
            }

            foreach (var handle in metadata.MethodDefinitions)
            {
                var method = metadata.GetMethodDefinition(handle);
                if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0
                    || (method.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL
                    || (method.ImplAttributes & MethodImplAttributes.ManagedMask) != MethodImplAttributes.Managed)
                {
                    return "Security policy: native, runtime-provided, and unmanaged methods are not available in the OJ.";
                }
            }

            foreach (var handle in metadata.MemberReferences)
            {
                var member = metadata.GetMemberReference(handle);
                var owner = GetParentTypeName(metadata, member.Parent);
                var memberName = metadata.GetString(member.Name);
                if (string.Equals(owner, "System.Console", StringComparison.Ordinal)
                    && memberName.StartsWith("OpenStandard", StringComparison.Ordinal))
                {
                    return "Security policy: raw standard stream handles are not available in the OJ.";
                }
            }

            foreach (var handle in metadata.CustomAttributes)
            {
                var attribute = metadata.GetCustomAttribute(handle);
                var owner = GetAttributeTypeName(metadata, attribute.Constructor);
                if (ContainsForbiddenAttributeName(owner))
                {
                    return "Security policy: compiled assembly contains a forbidden attribute.";
                }
            }

            return null;
        }
        catch (BadImageFormatException)
        {
            return "Security policy: compiler output is not a valid managed assembly.";
        }
        catch (Exception)
        {
            return "Security policy: compiled assembly could not be verified.";
        }
    }

    private static bool ContainsForbiddenAttributeName(string fullName)
        => fullName.Contains("DllImport", StringComparison.OrdinalIgnoreCase)
            || fullName.Contains("LibraryImport", StringComparison.OrdinalIgnoreCase)
            || fullName.Contains("UnmanagedCallersOnly", StringComparison.OrdinalIgnoreCase)
            || fullName.Contains("ModuleInitializer", StringComparison.OrdinalIgnoreCase)
            || fullName.Contains("SuppressUnmanagedCodeSecurity", StringComparison.OrdinalIgnoreCase);

    private static string GetTypeName(MetadataReader metadata, TypeReferenceHandle handle)
    {
        var type = metadata.GetTypeReference(handle);
        var ns = metadata.GetString(type.Namespace);
        var name = metadata.GetString(type.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string GetTypeName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        var ns = metadata.GetString(type.Namespace);
        var name = metadata.GetString(type.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string GetParentTypeName(MetadataReader metadata, EntityHandle handle)
        => handle.Kind switch
        {
            HandleKind.TypeReference => GetTypeName(metadata, (TypeReferenceHandle)handle),
            HandleKind.TypeDefinition => GetTypeName(metadata, (TypeDefinitionHandle)handle),
            _ => string.Empty
        };

    private static string GetAttributeTypeName(MetadataReader metadata, EntityHandle constructor)
    {
        return constructor.Kind switch
        {
            HandleKind.MemberReference => GetParentTypeName(metadata, metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent),
            HandleKind.MethodDefinition => GetTypeName(metadata, metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()),
            _ => string.Empty
        };
    }
}
