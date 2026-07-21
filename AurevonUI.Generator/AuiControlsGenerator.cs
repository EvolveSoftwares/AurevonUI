using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AurevonUI.Generator;

[Generator]
public sealed class AuiControlsGenerator : IIncrementalGenerator
{
    private static readonly HashSet<string> _visual_tags = new()
    {
        "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "use", "image", "text", "svg", "a",
    };

    private static readonly HashSet<string> _non_visual = new()
    {
        "defs", "clipPath", "mask", "filter", "linearGradient", "radialGradient",
        "pattern", "symbol", "style", "metadata", "title", "desc",
    };

    private static readonly HashSet<string> _aui_ignore_tags = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "defs", "style", "resources", "window", "aui", "aurevonui"
    };

    private static readonly object _watch_lock = new object();

    private static readonly Dictionary<string, FileSystemWatcher> _watchers =
        new Dictionary<string, FileSystemWatcher>(System.StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, System.Threading.Timer> _debounce_timers =
        new Dictionary<string, System.Threading.Timer>(System.StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> _csharp_keywords = new()
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    };

    public void Initialize(IncrementalGeneratorInitializationContext Context)
    {
        var UiFiles = Context.AdditionalTextsProvider
            .Where(static F => F.Path.EndsWith(".svg", System.StringComparison.OrdinalIgnoreCase)
                            || F.Path.EndsWith(".aui", System.StringComparison.OrdinalIgnoreCase))
            .Select(static (F, Ct) => (F.Path, Text: F.GetText(Ct)?.ToString() ?? string.Empty))
            .Collect();

        Context.RegisterSourceOutput(UiFiles, static (Spc, Files) => SyncAuiFiles(Files));

        var Classes = Context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (Node, _) => Node is ClassDeclarationSyntax C && C.BaseList is not null,
                transform: static (Ctx, _) => GetTarget((ClassDeclarationSyntax)Ctx.Node))
            .Where(static T => T is not null)
            .Select(static (T, _) => T!.Value);

        var SvgFiles = Context.AdditionalTextsProvider
            .Where(static F => F.Path.EndsWith(".svg", System.StringComparison.OrdinalIgnoreCase)
                            || F.Path.EndsWith(".aui", System.StringComparison.OrdinalIgnoreCase))
            .Select(static (F, Ct) => (Path: F.Path, Name: Path.GetFileName(F.Path), Text: F.GetText(Ct)?.ToString() ?? string.Empty))
            .Collect();

        var Combined = Classes.Combine(SvgFiles);

        Context.RegisterSourceOutput(Combined, static (Spc, Pair) =>
        {
            var Tgt = Pair.Left;
            var Files = Pair.Right;

            var MainFile = Files.FirstOrDefault(F =>
                string.Equals(F.Name, Tgt.SvgName, System.StringComparison.OrdinalIgnoreCase));
            if (MainFile.Name is null)
                return;

            string MainText = FreshText(MainFile.Path, MainFile.Text);
            if (string.IsNullOrEmpty(MainText))
                return;

            string? SvgText = null, AuiText = null;
            if (MainFile.Name.EndsWith(".aui", System.StringComparison.OrdinalIgnoreCase))
                AuiText = MainText;
            else
                SvgText = MainText;

            var CompanionName = GetCompanionName(MainFile.Name, MainText);
            if (CompanionName is not null)
            {
                var Companion = Files.FirstOrDefault(F =>
                    string.Equals(F.Name, CompanionName, System.StringComparison.OrdinalIgnoreCase));
                if (Companion.Name is not null)
                {
                    string CompanionText = FreshText(Companion.Path, Companion.Text);
                    if (CompanionText.Length > 0)
                    {
                        if (CompanionName.EndsWith(".aui", System.StringComparison.OrdinalIgnoreCase))
                            AuiText = CompanionText;
                        else
                            SvgText = CompanionText;
                    }
                }
            }

            var Typed = AuiSyncCore.CollectTypedControls(SvgText ?? string.Empty, AuiText);

            if (Typed.Count == 0 && AuiText is not null)
            {
                var Ids = new List<string>();
                CollectControlIds(AuiText, Ids, new HashSet<string>());
                foreach (var Id in Ids)
                    Typed.Add((Id, "Control"));
            }

            if (Typed.Count == 0)
                return;

            var Handlers = CollectEventHandlers(SvgText, AuiText);
            var Src = Emit(Tgt, Typed, Handlers);
            Spc.AddSource($"{Tgt.ClassName}.Controls.g.cs", SourceText.From(Src, Encoding.UTF8));
        });
    }

    private readonly struct Target
    {
        public readonly string Namespace;
        public readonly string ClassName;
        public readonly string SvgName;

        public Target(string Ns, string Cls, string Svg)
        {
            Namespace = Ns;
            ClassName = Cls;
            SvgName = Svg;
        }
    }

    private static Target? GetTarget(ClassDeclarationSyntax Cls)
    {
        bool Inherits = Cls.BaseList!.Types.Any(T => LocalName(T.Type) == "AuiWindow");
        if (!Inherits)
            return null;

        string? Svg = null;
        foreach (var M in Cls.Members)
        {
            if (M is ConstructorDeclarationSyntax Ctor
                && Ctor.Initializer is { } Init
                && Init.ThisOrBaseKeyword.Text == "base"
                && Init.ArgumentList.Arguments.Count > 0
                && Init.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax Lit
                && Lit.Token.Value is string S)
            {
                Svg = S;
                break;
            }
        }
        if (Svg is null)
            return null;

        return new Target(GetNamespace(Cls), Cls.Identifier.Text, Svg);
    }

    private static string LocalName(TypeSyntax T) => T switch
    {
        IdentifierNameSyntax Id => Id.Identifier.Text,
        QualifiedNameSyntax Q => Q.Right.Identifier.Text,
        GenericNameSyntax G => G.Identifier.Text,
        _ => T.ToString(),
    };

    private static string GetNamespace(SyntaxNode Node)
    {
        for (var P = Node.Parent; P is not null; P = P.Parent)
        {
            if (P is FileScopedNamespaceDeclarationSyntax Fs) return Fs.Name.ToString();
            if (P is NamespaceDeclarationSyntax Ns) return Ns.Name.ToString();
        }
        return string.Empty;
    }

#pragma warning disable RS1035

    private static void SyncAuiFiles(ImmutableArray<(string Path, string Text)> Files)
    {
        var Dirs = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var F in Files)
        {
            var Dir = Path.GetDirectoryName(F.Path);
            if (Dir is not null && Dir.Length > 0)
                Dirs.Add(Dir);
        }
        foreach (var Dir in Dirs)
        {
            SyncDirectory(Dir);
            EnsureWatcher(Dir);
        }
    }

    private static void SyncDirectory(string Dir)
    {
        try
        {
            foreach (var AuiPath in Directory.GetFiles(Dir, "*.aui"))
            {
                try
                {
                    var AuiText = AuiSyncCore.TryReadFile(AuiPath);
                    if (string.IsNullOrEmpty(AuiText))
                        continue;

                    var SvgName = GetCompanionName(Path.GetFileName(AuiPath), AuiText!);
                    if (SvgName is null)
                        continue;
                    var SvgText = AuiSyncCore.TryReadFile(Path.Combine(Dir, SvgName));
                    if (string.IsNullOrEmpty(SvgText))
                        continue;

                    var Synced = AuiSyncCore.ComputeSyncedAui(SvgText!, AuiText!);
                    if (Synced is not null)
                        AuiSyncCore.WriteIfChanged(AuiPath, Synced);

                    var Ns = AuiSyncCore.ParseXmlLenient(Synced ?? AuiText!).Root?.Name.NamespaceName ?? string.Empty;
                    var Ids = AuiSyncCore.CollectSvgControls(AuiSyncCore.ParseXmlLenient(SvgText!))
                        .Select(C => C.Id).ToList();
                    var XsdPath = Path.Combine(Dir, "AurevonUI.xsd");
                    AuiSyncCore.WriteIfChanged(XsdPath, AuiSyncCore.GenerateXsd(Ids, Ns));
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static void EnsureWatcher(string Dir)
    {
        lock (_watch_lock)
        {
            if (_watchers.ContainsKey(Dir))
                return;
            try
            {
                var Watcher = new FileSystemWatcher(Dir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                void OnChange(object Sender, FileSystemEventArgs E)
                {
                    if (E.Name is null)
                        return;
                    if (!E.Name.EndsWith(".svg", System.StringComparison.OrdinalIgnoreCase) &&
                        !E.Name.EndsWith(".aui", System.StringComparison.OrdinalIgnoreCase))
                        return;
                    ScheduleSync(Dir);
                }
                Watcher.Changed += OnChange;
                Watcher.Created += OnChange;
                Watcher.Renamed += (S, E) => OnChange(S, E);
                Watcher.EnableRaisingEvents = true;
                _watchers[Dir] = Watcher;
            }
            catch
            {
            }
        }
    }

    private static void ScheduleSync(string Dir)
    {
        lock (_watch_lock)
        {
            if (_debounce_timers.TryGetValue(Dir, out var Timer))
                Timer.Change(350, System.Threading.Timeout.Infinite);
            else
                _debounce_timers[Dir] = new System.Threading.Timer(
                    _ => SyncDirectory(Dir), null, 350, System.Threading.Timeout.Infinite);
        }
    }

#pragma warning restore RS1035

    private static string FreshText(string? FilePath, string Fallback)
    {
        if (!string.IsNullOrEmpty(FilePath))
        {
            var Disk = AuiSyncCore.TryReadFile(FilePath!);
            if (!string.IsNullOrEmpty(Disk))
                return Disk!;
        }
        return Fallback ?? string.Empty;
    }

    private static bool IsTemplateId(string? Id) =>
        Id is not null && Id.IndexOf("template", System.StringComparison.OrdinalIgnoreCase) >= 0;

    private static XDocument LoadXml(string Text)
    {
        var Settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var Reader = XmlReader.Create(new StringReader(Text), Settings);
        return XDocument.Load(Reader);
    }

    private static string? GetCompanionName(string Name, string Text)
    {
        if (Name.EndsWith(".aui", System.StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var Root = LoadXml(Text).Root;
                var SvgAttr = Root?.Attributes().FirstOrDefault(A =>
                    string.Equals(A.Name.LocalName, "Svg", System.StringComparison.OrdinalIgnoreCase));
                if (SvgAttr is not null)
                    return Path.GetFileName(SvgAttr.Value);
            }
            catch { }
            return Path.ChangeExtension(Name, ".svg");
        }
        if (Name.EndsWith(".svg", System.StringComparison.OrdinalIgnoreCase))
            return Path.ChangeExtension(Name, ".aui");
        return null;
    }

    private static void CollectControlIds(string FileText, List<string> Result, HashSet<string> Seen)
    {
        XDocument Doc;
        try
        {
            Doc = LoadXml(FileText);
        }
        catch
        {
            return;
        }

        var Root = Doc.Root;
        if (Root is null)
            return;

        bool IsAui = !string.Equals(Root.Name.LocalName, "svg", System.StringComparison.OrdinalIgnoreCase);

        if (IsAui)
        {
            foreach (var El in Root.Descendants())
            {
                if (_aui_ignore_tags.Contains(El.Name.LocalName))
                    continue;

                var Id = (string?)El.Attribute("id") ?? (string?)El.Attribute("Id") ?? (string?)El.Attribute("Name");
                if (Id is null && !El.Name.LocalName.Equals("Control", System.StringComparison.OrdinalIgnoreCase))
                    Id = El.Name.LocalName;
                if (Id is null || IsTemplateId(Id) || Seen.Contains(Id))
                    continue;

                Seen.Add(Id);
                Result.Add(Id);
            }
        }
        else
        {
            foreach (var El in Root.Descendants())
            {
                var Id = (string?)El.Attribute("id");
                if (Id is null || Seen.Contains(Id))
                    continue;
                if (!_visual_tags.Contains(El.Name.LocalName))
                    continue;
                if (El.Ancestors().Any(A => _non_visual.Contains(A.Name.LocalName)))
                    continue;
                if (El.AncestorsAndSelf().Any(A => IsTemplateId((string?)A.Attribute("id"))))
                    continue;

                Seen.Add(Id);
                Result.Add(Id);
            }
        }
    }

    private static List<string> CollectEventHandlers(string? SvgText, string? AuiText)
    {
        var Handlers = new HashSet<string>(System.StringComparer.Ordinal);
        void Extract(string? Text)
        {
            if (string.IsNullOrEmpty(Text)) return;
            try
            {
                var Doc = LoadXml(Text!);
                foreach (var El in Doc.Descendants())
                {
                    foreach (var Attr in El.Attributes())
                    {
                        var Name = Attr.Name.LocalName;
                        if (string.Equals(Name, "Click", System.StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Name, "Press", System.StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Name, "HoverEnter", System.StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Name, "HoverLeave", System.StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Name, "Submit", System.StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(Attr.Value))
                                Handlers.Add(Attr.Value.Trim());
                        }
                    }
                }
            }
            catch { }
        }
        Extract(SvgText);
        Extract(AuiText);
        return Handlers.ToList();
    }

    private static string Emit(Target Tgt, List<(string Id, string TypeName)> Typed, List<string> EventHandlers)
    {
        var Sb = new StringBuilder();
        Sb.AppendLine("// <auto-generated/>");
        Sb.AppendLine("#nullable enable");
        Sb.AppendLine();

        bool HasNs = Tgt.Namespace.Length > 0;
        string Indent = HasNs ? "    " : "";
        if (HasNs)
        {
            Sb.Append("namespace ").Append(Tgt.Namespace).AppendLine();
            Sb.AppendLine("{");
        }

        Sb.Append(Indent).AppendLine("[global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)]");
        Sb.Append(Indent).Append("partial class ").Append(Tgt.ClassName).AppendLine();
        Sb.Append(Indent).AppendLine("{");

        if (EventHandlers.Count > 0)
        {
            Sb.Append(Indent).AppendLine("    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(\"Trimming\", \"IL2026\", Justification = \"Preserve event handlers\")]");
            foreach (var Handler in EventHandlers)
            {
                Sb.Append(Indent).Append("    [global::System.Diagnostics.CodeAnalysis.DynamicDependency(\"").Append(Escape(Handler)).AppendLine("\")]");
            }
            Sb.Append(Indent).AppendLine("    private void __PreserveEventHandlers() {}");
            Sb.AppendLine();
        }

        var UsedNames = new HashSet<string>();
        foreach (var (Id, TypeName) in Typed)
        {
            string Name = SafeIdentifier(Id);
            string Unique = Name;
            int N = 2;
            while (!UsedNames.Add(Unique))
                Unique = Name + (N++);

            string FullType = AuiSyncCore.FullTypeName(TypeName);
            Sb.Append(Indent).Append("    /// <summary>Control z SVG: id=\"").Append(Escape(Id))
              .Append("\" (typ ").Append(TypeName).AppendLine(").</summary>");
            Sb.Append(Indent).Append("    public ").Append(FullType).Append(' ').Append(Unique);
            if (TypeName == "Control")
                Sb.Append(" => Get(\"").Append(Escape(Id)).AppendLine("\");");
            else
                Sb.Append(" => Get<").Append(FullType).Append(">(\"").Append(Escape(Id)).AppendLine("\");");
        }

        Sb.Append(Indent).AppendLine("}");
        if (HasNs)
            Sb.AppendLine("}");

        return Sb.ToString();
    }

    private static string Escape(string S) => S.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string SafeIdentifier(string Id)
    {
        var Sb = new StringBuilder(Id.Length);
        foreach (char Ch in Id)
            Sb.Append(char.IsLetterOrDigit(Ch) || Ch == '_' ? Ch : '_');

        string S = Sb.ToString();
        if (S.Length == 0)
            S = "_";
        if (char.IsDigit(S[0]))
            S = "_" + S;
        if (_csharp_keywords.Contains(S))
            S = "@" + S;
        return S;
    }
}
