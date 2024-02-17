---
category: Development
author: alexrp
title: The QUIC Network Protocol
summary: Packet prioritization, compact encoding, and proper encryption.
---

When building a server emulator for a game, the typical approach is to simply
use the game's original network protocol. This is easy since it requires no
client-side changes, and it is usually good enough in terms of performance and
security. TERA is an unusual case, however:

* The network protocol is based on a single
  [TCP](https://en.wikipedia.org/wiki/Transmission_Control_Protocol) connection,
  thus performing very poorly on an unreliable network due to
  [head-of-line blocking](https://en.wikipedia.org/wiki/Head-of-line_blocking).
  This is especially bad for an action game like TERA where every millisecond
  matters.
* The [PIKE](https://en.wikipedia.org/wiki/Pike_(cipher)) key exchange happens
  in plaintext when the connection is established, completely undermining the
  encryption and rendering it trivially
  vulnerable to [MITM](https://en.wikipedia.org/wiki/Man-in-the-middle_attack).
* Packet serialization is wasteful. For arrays and strings, lots of payload
  space is spent on
  [pointer metadata](https://docs.vezel.dev/novadrop/game/network-protocol#complex-types)
  (some of it entirely unused), and the reader must chase these pointers to
  correctly read the data. No effort is made to
  [compress integers](https://en.wikipedia.org/wiki/Variable-length_code).

With TERA Arise, we are addressing the reliability and security problems right
from the beginning, and we will incrementally address the compactness issue as
the project progresses. We are doing this by implementing a new network protocol
based on [QUIC](https://en.wikipedia.org/wiki/QUIC), as well as a new packet
serialization format.

QUIC was designed to be a replacement for TCP with some improvements that are
particularly useful for [HTTP](https://en.wikipedia.org/wiki/HTTP). For example,
the connection handshake is faster, a single connection can have multiple
TCP-like streams open, and encryption through
[TLS](https://en.wikipedia.org/wiki/Transport_Layer_Security) is directly
integrated into the protocol. The faster handshake is not particularly
interesting for TERA Arise since our connections are long-lived, but the ability
to have multiple streams very much is, and the integrated encryption is also a
nice boon for privacy and game integrity.

## Independent Prioritizable Streams

Packets are not created equal; some are clearly more important than others. The
primary player activity in TERA is combat, during which we care more about the
reliable delivery of packets related to skills and movement. Chat-related
packets are also somewhat important for coordination purposes, but not *as*
important. Meanwhile, packets related to other systems like guilds, friends, etc
are of very low importance.

In this way, we sort every packet into an importance category ahead of time. At
run time, we create a separate QUIC stream for each importance category.
Finally, we inform the underlying QUIC implementation of the relative importance
of each stream. At the moment, we have exactly 3 prioritized streams per
connection. This is an arbitrary number that can always be increased.

On a reliable network, this setup will ensure that packets queued for sending on
higher-priority streams go out first. There are also benefits for unreliable
networks; in particular, by separating packets into multiple streams,
intermittent packet loss will not necessarily harm all communication, and even
when there is packet loss on the higher-priority streams, the QUIC
implementation will know to prioritize re-sending packets for those streams
first.

It is important to note that, while all data transmission within a QUIC stream
is ordered exactly like a TCP connection, there are no ordering guarantees
*between* streams. So we do have to be careful not to accidentally introduce
ordering-related bugs when we sort packets into importance categories. For
example, it would be problematic for `S_SPAWN_USER` to be lower priority than
`S_ACTION_STAGE` since it could result in a client seeing a skill action from
a player that has not yet spawned in. But there would be nothing semantically
wrong with `S_SPAWN_USER` being *higher* priority than `S_ACTION_STAGE` (even if
that would not be particularly useful).

## Encryption and Mutual Authentication

Since TLS is mandatory in QUIC, we get encryption essentially for free, and
without MITM vulnerability.

In TLS, ordinarily, the client authenticates the server, but not the other way
around. Additionally, that authentication only checks that the server's
[certificate](https://en.wikipedia.org/wiki/Public_key_certificate) came from a
trusted
[certificate authority](https://en.wikipedia.org/wiki/Certificate_authority) and
that it is valid for the server's host name. We have stricter requirements for
authentication in TERA Arise, however:

* As part of our anti-cheating strategy, we do not want to permit clients
  to connect unless they are using a certificate that was issued for the exact
  TERA Arise client version corresponding to a particular TERA Arise server
  version.
* Because the server sends a
  [DLL](https://en.wikipedia.org/wiki/Dynamic-link_library) for the client to
  execute (also part of our anti-cheating strategy), it is imperative that the
  client can trust that the server is in fact a legitimate TERA Arise server
  matching the client version, lest the client end up running malicious code
  from an impostor.

To solve these problems, we do the following at build time:

1. We generate a custom certificate authority.
2. We generate a client private key and issue a certificate from the authority.
   This certificate is marked as valid for client use only.
3. We generate a server private key and issue a certificate from the authority.
   This certificate is marked as valid for server use only.
4. We embed the client private key and certificate in the client executable.
5. We embed the server private key and certificate in the server executable.
6. We embed the authority's certificate in the client and server executables.
   (But *not* its private key!)

With this, the protocol layer simply checks that the other side of the
connection can demonstrate mastery over the certificate it presented (i.e. that
it has the corresponding private key), checks that the certificate is valid, and
finally, checks that the certificate was issued by the same custom certificate
authority.

If these checks pass for a remote server, then it follows that the server
version matches the client version, and the DLL it sends is safe to run. If
these checks pass for a remote client, then it follows that the client version
matches the server version, and it should be permitted to connect.

I will describe our full anti-cheating strategy in an upcoming article.

## Compact Serialization Format

To compress integers, we use a scheme similar to
[length-encoded integers in MariaDB](https://mariadb.com/kb/en/protocol-data-types/#length-encoded-integers):

```csharp
public void WriteCompactUInt32(uint value)
{
    switch (value)
    {
        case < 0xfd:
            WriteByte((byte)value);
            break;
        case <= 0xffff:
            WriteByte(0xfd);
            WriteUInt16((ushort)value);
            break;
        case <= 0xffffff:
            WriteByte(0xfe);
            WriteUInt16((ushort)value);
            WriteByte((byte)(value >> 16));
            break;
        default:
            WriteByte(0xff);
            WriteUInt32(value);
            break;
    }
}

public uint ReadCompactUInt32()
{
    return ReadByte() switch
    {
        0xfd => ReadUInt16(),
        0xfe => (uint)(ReadUInt16() | ReadByte() << 16),
        0xff => ReadUInt32(),
        var value => value,
    };
}

public void WriteCompactInt32(int value)
{
    switch (value)
    {
        case >= -0x80 and < 0x7b:
            WriteSByte((sbyte)value);
            break;
        case >= -0x10000 and <= 0xffff:
            WriteSByte((sbyte)(value < 0 ? 0x7b : 0x7c));
            WriteUInt16((ushort)(value < 0 ? ~value : value));
            break;
        case >= -0x1000000 and <= 0xffffff:
            WriteSByte((sbyte)(value < 0 ? 0x7d : 0x7e));
            WriteUInt16((ushort)(value < 0 ? ~value : value));
            WriteByte((byte)((value < 0 ? ~value : value) >>> 16));
            break;
        default:
            WriteSByte(0x7f);
            WriteInt32(value);
            break;
    }
}

public int ReadCompactInt32()
{
    return ReadSByte() switch
    {
        0x7b => ~ReadUInt16(),
        0x7c => ReadUInt16(),
        0x7d => ~(ReadUInt16() | ReadByte() << 16),
        0x7e => ReadUInt16() | ReadByte() << 16,
        0x7f => ReadInt32(),
        var value => value,
    };
}
```

It is based on the simple observation that the vast majority of integer values
tend to be small. By reserving a few of the highest `byte` values (or lowest and
highest `sbyte` values for signed integers) as sentinels to indicate the integer
length, we can encode small values as a single byte, while for larger values,
the sentinel value indicates how many of the following bytes make up the value
(2, 3, or 4 bytes). The scheme naturally extends to any fixed-width integer
type, including e.g. `long`/`ulong`. The result is that most values get a
smaller encoding than a naive fixed-width encoding, while larger values (which
are very rare) get a slightly larger encoding.

For arrays and strings, we build on this integer compression scheme by writing
the element count as a compact `ushort`, followed immediately by all elements.
There is no pointer metadata; a reader can simply read the array or string right
where it is encountered. This saves a significant amount of space in more
complex packets such as `S_INVEN`.

Of course, we cannot simply change the serialization format for packets and
expect the client to understand the packet. Our plan is to incrementally replace
the client's native packet handlers with ones written by us, using our custom
serialization format. This has to be done incrementally because we have to
basically duplicate all the logic in the native packet handler, which in turn
requires reverse engineering.
