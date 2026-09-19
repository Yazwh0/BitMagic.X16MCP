using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace X16M.Resources;

[McpServerResourceType]
public static class SyntaxResources
{
    [McpServerResource(UriTemplate = "docs://bmasm-syntax", Name = "bmasm-syntax", MimeType = "text/markdown")]
    [Description("Purpose-written .bmasm/Template Engine syntax reference: directives, types, labels, scope/name resolution, expressions and the byte operators, embedded C#, the BM library, and a real worked example. Read this before writing or editing .bmasm code.")]
    public static string BmasmSyntax()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("X16M.Resources.BmasmSyntax.md")
            ?? throw new InvalidOperationException("Embedded resource X16M.Resources.BmasmSyntax.md is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
