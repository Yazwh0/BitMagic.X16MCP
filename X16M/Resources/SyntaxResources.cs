using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace X16M.Resources;

[McpServerResourceType]
public static class SyntaxResources
{
    [McpServerResource(UriTemplate = "docs://bmasm-syntax", Name = "bmasm-syntax", MimeType = "text/markdown")]
    [Description("Purpose-written .bmasm/Template Engine syntax reference: directives, types, labels, scope/name resolution, expressions and the byte operators, embedded C#, the BM library, and a real worked example. Read this before writing or editing .bmasm code.")]
    public static string BmasmSyntax() => ReadEmbedded("X16M.Resources.BmasmSyntax.md");

    [McpServerResource(UriTemplate = "docs://other-compilers", Name = "other-compilers", MimeType = "text/markdown")]
    [Description("How symbols from programs built by other compilers (eg ca65) are named and typed in the debugger: how to write them for evaluate and find them in get_variables. Read this when debugging a project that isn't BitMagic.")]
    public static string OtherCompilers() => ReadEmbedded("X16M.Resources.OtherCompilers.md");

    private static string ReadEmbedded(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
