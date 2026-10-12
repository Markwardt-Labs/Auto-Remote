# Syntax highlighting

Previewed text files are colored by a small tokenizer instead of a grammar engine such as TextMate. A grammar engine would add a large dependency to the Avalonia clients (including Android) and could not be shared with the dependency free web client; a tokenizer of a few hundred lines covers what a read-only preview needs. It lives in `Frontend/src/Syntax/` and is mirrored line for line in `WebClient/index.html` (`syntaxLanguages` through `syntaxNodes`), so the clients color a file identically.

## Languages are data

A language is a description, not code: keywords, comment markers, quote characters, and a few switches (capitalized names are types, a name before `(` is a function, `$name` is a variable, and so on). One generic scanner reads any such description. Families that share a format share a description, which is how many extensions get covered: `.csproj`, `.props`, `.axaml`, `.svg` and similar are `xml`; `.vue`, `.cshtml` and `.razor` are `html`; Java also serves Groovy, Gradle, Scala, Dart and proto files; `.gitignore`, `.dockerignore` and `.npmignore` share one description.

Three formats do not fit the generic scanner and have their own: markup (tags, attributes, comments, CDATA; HTML hands `<script>` and `<style>` content to the JavaScript and CSS descriptions), delimited rows (CSV and TSV, header row as properties, numbers colored) and line based keys. The last is part of the generic scanner: YAML, INI/TOML, `.env`, `.conf` and ignore files give a pattern for the key at the start of a line (and INI one for `[section]` headings).

## Detecting a file's language

A file is matched by exact name (`Dockerfile`, `Makefile`, `Gemfile`), then the `.env` and `Dockerfile.` prefixes, then its extension, taken after the last dot so a dotfile such as `.gitignore` matches as extension `gitignore`. When the name says nothing, the first line decides: a shebang (python, node, ruby, perl, php, PowerShell, any shell), `<?xml`, or an HTML doctype. Unknown files show as plain text.

## Scanning

The output is a list of tokens (kind, start, length); text between tokens is plain. The scanner makes one pass and never backtracks, so a stray quote or bracket only colors to the end of its line (or the end of the text for block comments and multi-line strings) and cannot hang or throw; tokens never overlap or leave the text. A `'` right after a letter or digit is not a quote, which keeps apostrophes in YAML, INI and shell from starting strings. Text over 100,000 characters is not highlighted at all, since the Avalonia client builds one run per token and the server already cuts files at 256 KB.

## Colors

The palette is the Visual Studio Code dark one, as `Syntax*` brushes in `ClientApp.axaml` and `--syn-*` variables in the web client. `SyntaxView` looks the brushes up when it renders, like `MarkdownView` does.

## Find and line numbers

Line numbers are a muted gutter beside the text; it is not selectable, so copying text never includes it. Find marks matches as a background behind the syntax colors: the text is split at every token and match boundary, so a match inside a keyword keeps the keyword's color. The current match has its own brighter mark and is scrolled into view only when the user steps to it, never when the file refreshes underneath. Rendered markdown is searched too, but over the text as laid out on screen (blocks joined by line breaks) rather than the source, so `MarkdownView` publishes that text, the matches index into it, and it re-renders with the matches marked.
