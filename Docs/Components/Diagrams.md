# Mermaid diagrams

A fenced block tagged `mermaid` in a previewed markdown file is drawn as a picture in every client. Chat messages are not touched: a Mermaid block there stays a code block.

## Server draws, clients show

The server turns the text into an SVG with Mermaider, a pure .NET library, so there is one implementation of Mermaid for the desktop, mobile and web clients, none of which would otherwise have a way to run Mermaid (no Node or browser engine is available on Android). The `RenderDiagram` command carries the text and answers the SVG. The server caches the SVG by a hash of the trimmed text and bounds the text length and the drawing time, so a pathological diagram answers an error instead of holding a connection.

The server shapes the picture so it reads well and looks the same everywhere:

- Colors: the options give Mermaider the dark theme's seven base colors, but it derives the rest (group fills, shape gradients, shadows) with CSS `color-mix()` and `var()`, which browsers evaluate and the Avalonia clients' SVG renderer does not, so shapes that used them were drawn black. After drawing, every such expression is evaluated on the server and replaced by the plain color it stands for, so all clients show identical colors. References to Mermaider's own derived variables stay (their definitions are plain colors by then), as do font size variables.
- Cycles: Mermaider's flowchart and state layout breaks cycles in an arbitrary order, which drew a retry loop as the main flow and put the entry state at the bottom. The layout is wrapped so that the edges that close a cycle (found by walking the graph from its first edge, so the order the author wrote it in decides) are laid out reversed and drawn backward afterwards, with their arrow ends and route swapped back. Edges between subgraphs follow the nodes inside them. Class, ER and the other diagram types are laid out as Mermaider does.

The remaining flaws are Mermaider's: edges of a state diagram that cross a composite state run through its box, and `o--o` and `x--x` edges of flowcharts are not drawn. The optional MSAGL layout was tried and is worse (it flips the flow and overlaps subgraphs).

## Clients

A block starts as a Show Diagram button so a long document does not ask the server for every diagram when it opens; pressing it asks for the drawing. A diagram that was drawn already (the clients remember the most recent ones) shows at once when the preview is built again, for example after a refresh of the file. While the server draws, the block says so; when it cannot, the block shows the reason above the Mermaid text. The picture scrolls sideways when wider than the panel and has zoom out, reset (showing the zoom share) and zoom in buttons above it, from 25% to 400% in steps of a quarter, also with Ctrl and the mouse wheel; zooming only scales the picture, the server is not asked again. Its text is not part of what find searches.

The web client shows the SVG as an image. The Avalonia clients rasterize it with Svg.Skia at three times the displayed size so it stays sharp on dense screens, capped at 4096 pixels on the longest side because a GPU cannot upload a larger texture and would show a broken image with no error.
