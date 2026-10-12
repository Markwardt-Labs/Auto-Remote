namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Knows the languages that can be highlighted and which files are written in them.
/// </summary>
internal interface ISyntaxLanguages
{
    /// <summary>
    /// Finds a language by name.
    /// </summary>
    /// <param name="name">The language's name.</param>
    /// <returns>The language, or <see langword="null" /> when it is unknown.</returns>
    SyntaxLanguage? Find(string name);

    /// <summary>
    /// Finds the language a file is written in, from its name and, when the name says nothing, its first line.
    /// </summary>
    /// <param name="path">The file's path.</param>
    /// <param name="text">The file's text.</param>
    /// <returns>The language, or <see langword="null" /> when it is not recognized.</returns>
    SyntaxLanguage? Detect(string path, string text);
}

/// <inheritdoc />
internal sealed class SyntaxLanguages : ISyntaxLanguages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SyntaxLanguages" /> class.
    /// </summary>
    public SyntaxLanguages()
    {
        Define("c", new SyntaxLanguage { Name = "c", Keywords = Words(CKeywords), LineComments = ["//"], BlockStart = "/*", BlockEnd = "*/", WordStart = "#", Functions = true }, "c h glsl hlsl shader frag vert cginc compute");
        Define("cpp", new SyntaxLanguage { Name = "cpp", Keywords = Words(CKeywords + CppKeywords), LineComments = ["//"], BlockStart = "/*", BlockEnd = "*/", WordStart = "#", Functions = true, Types = true }, "cpp cc cxx c++ hpp hh hxx h++ ino cu cuh");
        Define("cs", new SyntaxLanguage { Name = "cs", Keywords = Words("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while var async await yield record init required get set value when where with partial dynamic global nameof not and or #if #else #elif #endif #region #endregion #define #undef #pragma #nullable #warning #error #line"), LineComments = ["//"], BlockStart = "/*", BlockEnd = "*/", TripleQuotes = true, WordStart = "#", Functions = true, Types = true }, "cs csx");
        Define("java", new SyntaxLanguage { Name = "java", Keywords = Words("abstract assert boolean break byte case catch char class const continue default do double else enum extends final finally float for goto if implements import instanceof int interface long native new package private protected public return short static strictfp super switch synchronized this throw throws transient try void volatile while var record sealed permits yield true false null"), LineComments = ["//"], BlockStart = "/*", BlockEnd = "*/", TripleQuotes = true, WordStart = "@", Functions = true, Types = true }, "java groovy gradle scala dart proto");
        Define("kt", new SyntaxLanguage { Name = "kt", Keywords = Words("abstract actual annotation as break by catch class companion const constructor continue crossinline data delegate do dynamic else enum expect external false final finally for fun get if import in infix init inline inner interface internal is it lateinit lazy noinline null object open operator out override package private protected public reified return sealed set super suspend tailrec this throw true try typealias typeof val var vararg when where while"), LineComments = ["//"], BlockStart = "/*", BlockEnd = "*/", TripleQuotes = true, WordStart = "@", Functions = true, Types = true }, "kt kts");
        Define("go", new SyntaxLanguage { Name = "go", Keywords = Words("break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var true false nil iota bool byte complex128 error float32 float64 int int8 int16 int32 int64 rune string uint uint8 uint16 uint32 uint64 uintptr any"), LineComments = ["//"], BlockStart = "/*", BlockEnd = "*/", MultilineQuotes = "`", Quotes = "\"'`", Functions = true, Types = true }, "go");
        Define("rs", new SyntaxLanguage { Name = "rs", Keywords = Words("as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while macro_rules bool char str u8 u16 u32 u64 u128 usize i8 i16 i32 i64 i128 isize f32 f64"), LineComments = ["//"], BlockStart = "/*", BlockEnd = "*/", Lifetimes = true, Functions = true, Types = true }, "rs");
        Define("js", new SyntaxLanguage { Name = "js", Keywords = Words(JsKeywords), LineComments = ["//"], BlockStart = "/*", BlockEnd = "*/", Quotes = "\"'`", MultilineQuotes = "`", WordPart = "$", Functions = true, Types = true }, "js mjs cjs jsx");
        Define("ts", new SyntaxLanguage { Name = "ts", Keywords = Words(JsKeywords + " interface type enum implements namespace declare abstract readonly public private protected keyof infer satisfies unknown never any string number boolean symbol object bigint module override"), LineComments = ["//"], BlockStart = "/*", BlockEnd = "*/", Quotes = "\"'`", MultilineQuotes = "`", WordStart = "@", WordPart = "$", Functions = true, Types = true }, "ts tsx mts cts");
        Define("php", new SyntaxLanguage { Name = "php", Keywords = Words("abstract and array as break callable case catch class clone const continue declare default do echo else elseif empty enddeclare endfor endforeach endif endswitch endwhile enum extends final finally fn for foreach function global goto if implements include include_once instanceof insteadof interface isset list match namespace new null or print private protected public readonly require require_once return static switch throw trait true false try unset use var while xor yield self parent int float string bool void mixed never iterable object", true), LineComments = ["//", "#"], BlockStart = "/*", BlockEnd = "*/", VariableSigils = "$", Functions = true, Types = true }, "php phtml php3 php4 php5 php7 phps");
        Define("py", new SyntaxLanguage { Name = "py", Keywords = Words("and as assert async await break class continue def del elif else except False finally for from global if import in is lambda None nonlocal not or pass raise return True try while with yield match case func signal extends onready export"), LineComments = ["#"], TripleQuotes = true, WordStart = "@", Functions = true, Types = true }, "py pyw pyi gd");
        Define("rb", new SyntaxLanguage { Name = "rb", Keywords = Words("alias and begin BEGIN break case class def do else elsif end END ensure false for if in module next nil not or redo rescue retry return self super then true undef unless until when while yield require require_relative include extend attr_accessor attr_reader attr_writer puts private protected public lambda proc"), LineComments = ["#"], Quotes = "\"'`", VariableSigils = "@$", WordPart = "?!", Functions = true, Types = true }, "rb rake gemspec ru erb rbi");
        Define("pl", new SyntaxLanguage { Name = "pl", Keywords = Words("my our local sub use no package require if elsif else unless while until for foreach do return last next redo goto and or not xor eq ne lt gt le ge cmp print say shift push pop BEGIN END undef defined die warn qw"), LineComments = ["#"], VariableSigils = "$@%", Functions = true }, "pl pm t");
        Define("sql", new SyntaxLanguage { Name = "sql", Keywords = Words("select from where and or not insert into values update set delete create alter drop table index view database schema join inner outer left right full cross on as group by order having limit offset union all distinct null is in like between exists case when then else end primary key foreign references default unique constraint check begin commit rollback transaction with asc desc top count sum avg min max int integer bigint smallint varchar char text boolean bool date datetime timestamp decimal numeric float real serial truncate grant revoke procedure function trigger returns return declare if while exec execute use go true false", true), LineComments = ["--"], BlockStart = "/*", BlockEnd = "*/", Quotes = "\"'`", Functions = true }, "sql ddl dml psql mysql pgsql tsql");
        Define("sh", new SyntaxLanguage { Name = "sh", Keywords = Words("if then else elif fi case esac for select while until do done in function time coproc return exit break continue local export readonly declare unset shift source alias echo cd set eval exec trap test true false read printf"), LineComments = ["#"], Quotes = "\"'`", VariableSigils = "$", Functions = true }, "sh bash zsh ksh bashrc bash_profile bash_aliases bash_logout zshrc zprofile zshenv profile envrc");
        Define("ps1", new SyntaxLanguage { Name = "ps1", Keywords = Words("begin break catch class continue data do dynamicparam else elseif end enum exit filter finally for foreach from function if in param process return switch throw trap try until using while workflow", true), LineComments = ["#"], BlockStart = "<#", BlockEnd = "#>", VariableSigils = "$", WordPart = "-", Functions = true }, "ps1 psm1 psd1");
        Define("dockerfile", new SyntaxLanguage { Name = "dockerfile", Keywords = Words("from run cmd label maintainer expose env add copy entrypoint volume user workdir arg onbuild stopsignal healthcheck shell as", true), LineComments = ["#"], VariableSigils = "$" }, "dockerfile containerfile");
        Define("make", new SyntaxLanguage { Name = "make", Keywords = Words("ifeq ifneq ifdef ifndef else endif include sinclude define endef export unexport override vpath"), LineComments = ["#"], VariableSigils = "$", WordStart = "." }, "mk make makefile");
        Define("css", new SyntaxLanguage { Name = "css", Keywords = Words("@media @import @font-face @keyframes @charset @supports @page @namespace @layer @container @property @tailwind @apply"), BlockStart = "/*", BlockEnd = "*/", WordStart = "@-", WordPart = "-", HexColors = true, Functions = true, ColonProperties = true }, "css");
        Define("scss", new SyntaxLanguage { Name = "scss", Keywords = Words("@media @import @font-face @keyframes @charset @supports @page @namespace @layer @container @mixin @include @extend @use @forward @function @return @if @else @each @for @while @tailwind @apply"), LineComments = ["//"], BlockStart = "/*", BlockEnd = "*/", WordStart = "@-", WordPart = "-", VariableSigils = "$", HexColors = true, Functions = true, ColonProperties = true }, "scss sass less styl");
        Define("json", new SyntaxLanguage { Name = "json", Keywords = Words("true false null"), LineComments = ["//"], BlockStart = "/*", BlockEnd = "*/", Quotes = "\"", QuotedKeys = true }, "json jsonc json5 geojson webmanifest map jsonl ndjson babelrc eslintrc prettierrc code-workspace");
        Define("yaml", new SyntaxLanguage { Name = "yaml", Keywords = Words("true false null True False Null"), LineComments = ["#"], QuotedKeys = true, LineKey = Anchored(@"(?:-[ \t]+)*([^\s#:'""\[\]{},&*!|>%@-][^#:\n]*?)(?=:(?:[ \t\r\n]|$))") }, "yaml yml clang-format");
        Define("ini", new SyntaxLanguage { Name = "ini", Keywords = Words("true false"), LineComments = [";", "#"], Section = Anchored(@"\[\[?[^\]\n]*\]\]?"), LineKey = Anchored(@"[^\s=;#\[][^=\n]*?(?=[ \t]*=)") }, "ini cfg properties toml editorconfig desktop service gitconfig gitmodules npmrc tscn tres godot");
        Define("env", new SyntaxLanguage { Name = "env", LineComments = ["#"], VariableSigils = "$", LineKey = Anchored(@"(?:export[ \t]+)?([A-Za-z_][\w.\-]*)(?=[ \t]*=)") }, "env");
        Define("conf", new SyntaxLanguage { Name = "conf", LineComments = ["#"], VariableSigils = "$", LineKey = Anchored(@"[A-Za-z_][\w.\-:]*") }, "conf cnf htaccess");
        Define("ignore", new SyntaxLanguage { Name = "ignore", LineComments = ["#"], Quotes = string.Empty, LineKey = Anchored(@"[^\s#]\S*") }, "gitignore dockerignore npmignore slugignore");
        Define("gitattributes", new SyntaxLanguage { Name = "gitattributes", Keywords = Words("text binary eol diff merge filter export-ignore export-subst linguist-generated linguist-vendored linguist-detectable linguist-language"), LineComments = ["#"], Quotes = "\"", WordPart = "-", LineKey = Anchored(@"[^\s#]\S*") }, "gitattributes");
        Define("sln", new SyntaxLanguage { Name = "sln", Keywords = Words("Project EndProject Global EndGlobal GlobalSection EndGlobalSection ProjectSection EndProjectSection preSolution postSolution preProject postProject VisualStudioVersion MinimumVisualStudioVersion"), LineComments = ["#"], Quotes = "\"", Functions = true }, "sln");
        Define("csv", new SyntaxLanguage { Name = "csv", Delimiter = ',' }, "csv");
        Define("tsv", new SyntaxLanguage { Name = "tsv", Delimiter = '\t' }, "tsv");
        Define("xml", new SyntaxLanguage { Name = "xml", Markup = true }, "xml csproj fsproj vbproj vcxproj props targets nuspec resx config xaml axaml slnx svg xsd xsl xslt plist manifest wsdl ruleset appxmanifest vsixmanifest proj dotsettings storyboard kml rss atom xlf");
        Define("html", new SyntaxLanguage { Name = "html", Markup = true, EmbedsCode = true }, "html htm xhtml cshtml razor vue svelte astro mjml");
        names["dockerfile"] = "dockerfile";
        names["containerfile"] = "dockerfile";
        names["makefile"] = "make";
        names["gnumakefile"] = "make";
        names["gemfile"] = "rb";
        names["rakefile"] = "rb";
        names["gitattributes"] = "gitattributes";
    }

    private readonly Dictionary<string, SyntaxLanguage> languages = new();

    private readonly Dictionary<string, string> extensions = new();

    private readonly Dictionary<string, string> names = new();

    private string CKeywords { get; } = "auto break case char const continue default do double else enum extern float for goto if inline int long register restrict return short signed sizeof static struct switch typedef union unsigned void volatile while bool true false NULL #include #define #undef #if #ifdef #ifndef #elif #else #endif #pragma #error #line #warning ";

    private string CppKeywords { get; } = "alignas alignof and asm catch class constexpr consteval constinit decltype delete dynamic_cast explicit export final friend mutable namespace new noexcept not nullptr operator or override private protected public reinterpret_cast static_assert static_cast template this thread_local throw try typeid typename using virtual xor concept requires co_await co_return co_yield #import";

    private string JsKeywords { get; } = "break case catch class const continue debugger default delete do else export extends finally for function if import in instanceof let new of return super switch this throw try typeof var void while with yield async await static get set from as true false null undefined NaN Infinity";

    /// <inheritdoc />
    public SyntaxLanguage? Find(string name) => languages.GetValueOrDefault(name);

    /// <inheritdoc />
    public SyntaxLanguage? Detect(string path, string text)
    {
        string file = path[(path.LastIndexOfAny(['/', '\\']) + 1)..].ToLowerInvariant();
        string? language = null;

        if (names.TryGetValue(file, out string? named))
        {
            language = named;
        }
        else if (file == ".env" || file.StartsWith(".env.", StringComparison.Ordinal))
        {
            language = "env";
        }
        else if (file.StartsWith("dockerfile.", StringComparison.Ordinal))
        {
            language = "dockerfile";
        }
        else if (file.Contains('.') && extensions.TryGetValue(file[(file.LastIndexOf('.') + 1)..], out string? known))
        {
            language = known;
        }

        return Find(language ?? Sniff(text) ?? string.Empty);
    }

    private Regex Anchored(string pattern) => new(@"\G(?:" + pattern + ")", RegexOptions.CultureInvariant);

    private HashSet<string> Words(string words, bool ignoreCase = false) => new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private void Define(string name, SyntaxLanguage language, string extensionList)
    {
        languages[name] = language;

        foreach (string extension in extensionList.Split(' '))
        {
            extensions[extension] = name;
        }
    }

    private string? Sniff(string text)
    {
        int end = text.IndexOf('\n');
        string first = (end < 0 ? text : text[..end]).TrimStart('﻿').Trim();

        if (first.StartsWith("#!", StringComparison.Ordinal))
        {
            return first switch
            {
                _ when first.Contains("python") => "py",
                _ when first.Contains("node") || first.Contains("deno") || first.Contains("bun") => "js",
                _ when first.Contains("ruby") => "rb",
                _ when first.Contains("perl") => "pl",
                _ when first.Contains("php") => "php",
                _ when first.Contains("pwsh") || first.Contains("powershell") => "ps1",
                _ when first.Contains("sh") => "sh",
                _ => null,
            };
        }

        if (first.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            return "xml";
        }

        return first.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || first.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ? "html" : null;
    }
}
