using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Runner.Services;

internal static class ManagedPeSecurityPolicy
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "System.Diagnostics",
        "System.IO",
        "System.Net",
        "System.Reflection",
        "System.Runtime.InteropServices",
        "System.Runtime.Loader",
        "System.Linq.Expressions",
        "System.CodeDom",
        "System.Security",
        "System.Management",
        "System.DirectoryServices",
        "Microsoft.Win32",
        "Microsoft.CSharp.RuntimeBinder"
    ];

    private static readonly string[] ForbiddenTypeNames =
    [
        "System.Environment",
        "System.AppDomain",
        "System.Activator",
        "System.Type",
        "System.Delegate",
        "System.Threading.Thread",
        "System.Threading.ThreadPool",
        "System.Runtime.CompilerServices.RuntimeHelpers",
        "System.Runtime.CompilerServices.Unsafe",
        "System.Runtime.CompilerServices.UnmanagedCallersOnlyAttribute",
        "System.Runtime.CompilerServices.ModuleInitializerAttribute"
    ];

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
            foreach (var handle in metadata.TypeReferences)
            {
                if (IsForbidden(GetTypeName(metadata, handle)))
                {
                    return "Security policy: compiled assembly references a forbidden framework API.";
                }
            }

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
                if (IsForbidden(owner))
                {
                    return "Security policy: compiled assembly references a forbidden framework API.";
                }
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
                if (IsForbidden(owner)
                    || owner.Contains("DllImport", StringComparison.OrdinalIgnoreCase)
                    || owner.Contains("LibraryImport", StringComparison.OrdinalIgnoreCase)
                    || owner.Contains("UnmanagedCallersOnly", StringComparison.OrdinalIgnoreCase)
                    || owner.Contains("ModuleInitializer", StringComparison.OrdinalIgnoreCase))
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

    private static bool IsForbidden(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return false;
        if (ForbiddenTypeNames.Any(value => string.Equals(value, fullName, StringComparison.Ordinal)))
        {
            return true;
        }
        return ForbiddenNamespacePrefixes.Any(prefix =>
            string.Equals(fullName, prefix, StringComparison.Ordinal)
            || fullName.StartsWith(prefix + ".", StringComparison.Ordinal));
    }

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
