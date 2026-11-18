# Local connect

A desktop client offers a button on its connect screen for each server on the same machine that allows the account running the client, so nobody types a port and key for a server on their own machine. It works the same when the server is a service.

## Why a channel and not a shortcut

An address that is local says nothing about who is asking: a Cloudflare tunnel, a reverse proxy, an SSH forward and a web page in the browser all arrive from `127.0.0.1`. So the server never trusts the address. It hands the key out through a channel only an operating system account can reach, and checks which account is asking.

## The channel

When the server listens it also opens a local channel, named after its port so that several servers on one machine do not collide:

- On Windows a named pipe `AutoRemote-<port>`, created with an ACL: the account running the server, plus the accounts the config allows. Without `localUsers` the ACL allows interactive logons (the `INTERACTIVE` identity), which service accounts and `SYSTEM` do not have.
- On Linux a Unix socket `<port>.sock` in `$XDG_RUNTIME_DIR/auto-remote` (set for a systemd user service and for a login session) and in `/run/auto-remote` (create it for a system service with `RuntimeDirectory=auto-remote` in the unit). The server binds every folder it can write. It is not in `/tmp`, which a service with `PrivateTmp` cannot share. The socket may be connected to by anyone who can reach its folder; the server reads the user id of each peer from the kernel (`SO_PEERCRED`) and answers only the allowed ones. A refused peer gets an empty answer.
- On other platforms nothing is opened.

An allowed peer gets one JSON line with the host (`127.0.0.1`), port, access key, server version and certificate fingerprint, and the channel closes. The server writes nothing to its log for this; it only logs when the channel cannot be opened, and the server runs on without it.

## Who is allowed

`localUsers` in the server config decides:

- Not set (the default): every regular user account, so a one person machine works with no setup whether the server runs as that person or as an account of its own. On Linux that is a user id from `UID_MIN` (read from `/etc/login.defs`, 1000 when absent) up, except `nobody`; the accounts of the system and its services are left out, so a compromised service cannot take the key.
- An empty list: the channel is not opened at all.
- A list of account names: exactly those accounts, even system ones (`root` can be listed). Names that do not exist are ignored, an empty name is a config error. On a machine shared with other people, list the accounts.

The list is read at startup like the rest of the config.

## The client

`ILocalServers` looks in the folders (or the pipe namespace) for servers and asks each one; those that answer are offered, one button each (`Connect to Local Server`, with the port when there are several). The connect screen looks again every few seconds so that a server that starts later shows up, and stops when another screen replaces it.

A local connection is saved as a marker (`local`, with the port saying which server) and no key. Every connect, including the retries after a restart, the wait during an update and every reconnect of a lost connection, resolves it again: `IServerConnection.Factory` asks the channel, then makes the ordinary connection with the key it was just given and refuses a server whose certificate is not the announced fingerprint. So a rotated key or a changed certificate needs nothing from the user, and removing an account from `localUsers` stops it at its next start. A refusal is shown as "not allowed" and is not retried; a missing channel is retried like an unreachable server.

Only the desktop client has this: the key never leaves the machine, so it means nothing to a phone, and a browser cannot reach the channel.
