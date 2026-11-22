# Mermaid diagrams

A fenced block tagged `mermaid` in a previewed markdown file is drawn as a picture in every client. Chat messages are not touched: a Mermaid block there stays a code block.

## Server draws, clients show

The server turns the text into an SVG with Mermaider, a pure .NET library, so there is one implementation of Mermaid for the desktop, mobile and web clients, none of which would otherwise have a way to run Mermaid (no Node or browser engine is available on Android). The `RenderDiagram` command carries the text and answers the SVG. The server caches the SVG by a hash of the trimmed text and bounds the text length and the drawing time, so a pathological diagram answers an error instead of holding a connection.

The colors are explicit hex values for the clients' dark theme. Mermaider derives line, surface and border shades with CSS `color-mix()` unless they are given, and the Avalonia clients' SVG renderer does not evaluate it, which left lines and table borders invisible. An ER diagram relationship from an entity to itself is dropped before drawing because Mermaider fails on it.

## Clients

A block starts as a Show Diagram button so a long document does not ask the server for every diagram when it opens; pressing it asks for the drawing. A diagram that was drawn already (the clients remember the most recent ones) shows at once when the preview is built again, for example after a refresh of the file. While the server draws, the block says so; when it cannot, the block shows the reason above the Mermaid text. The picture scrolls sideways when wider than the panel and has zoom out, reset (showing the zoom share) and zoom in buttons above it, from 25% to 400% in steps of a quarter, also with Ctrl and the mouse wheel; zooming only scales the picture, the server is not asked again. Its text is not part of what find searches.

The web client shows the SVG as an image. The Avalonia clients rasterize it with Svg.Skia at three times the displayed size so it stays sharp on dense screens, capped at 4096 pixels on the longest side because a GPU cannot upload a larger texture and would show a broken image with no error.
