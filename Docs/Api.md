# API

The public API is the shared surface of the `Core` library consumed by both `Server` and `Client`: `IAppInfo` for product identity, and the types that make up the wire protocol. This document covers the design and flow of that surface using public types only.

## Product identity

A caller obtains an `IAppInfo` from `IAppInfo.Factory`, passing the assembly whose version should be reported. `IAppInfo` carries the product name and the `major.minor.patch` version, so the server and the client identify themselves identically. The factory is nested in the interface so construction and DI registration read as `IAppInfo.IFactory`/`new IAppInfo.Factory()`.

## Wire protocol

`HandshakeRequest` and `HandshakeResponse` carry the access key exchange that opens a connection. `RemoteRequest` and `RemoteResponse` are the two messages after it; `RemoteCommand` names the operations and says which request fields each uses and which response field answers it. The payload types are `WorkspaceInfo` (a workspace, named after its folder, with its AI model, effort, attached AI session and group), `WorkspaceCatalog`/`WorkspaceGroup` (the workspaces and the groups that organize them, groups nesting through their parent), `FolderListing`/`FolderEntry` (browsing the server's machine to choose a workspace folder), `WorkspaceFiles`/`FileEntry`/`FileContent` (reading inside a workspace folder), `ChatMessage` with `MessageRole` and `ChatHistory` (a chunk of the conversation), and `RemoteEvent` (what the server pushes: new messages, the AI starting or stopping work, file changes in a watched folder, failures). A server-side refusal surfaces on the client as `RemoteException`.

`IMessageChannel` reads and writes newline-delimited text and JSON over any stream, and is created through `IMessageChannel.Factory`.

## Flow

1. The app's entry point creates an `IAppInfo.Factory` and calls `Create` with its own assembly.
2. A connecting side creates a channel over its stream, completes the access key handshake, then exchanges `RemoteRequest` and `RemoteResponse` lines; a response with id zero is a pushed `RemoteEvent`.
