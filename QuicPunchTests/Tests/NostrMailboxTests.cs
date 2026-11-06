using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using QuicPunch;

namespace QuicPunchTests.Tests;

public static class NostrMailboxTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("     NOSTR ASYNCHRONOUS STORE & FORWARD TESTS     ");
        Console.WriteLine("==================================================");

        // Test 1: Key generation and ECDH Shared Secret Symmetry
        Console.Write("[TEST 1] Secp256k1 ECDH Shared Secret symmetry... ");
        byte[] alicePriv = NostrSchnorr.CreatePrivateKey();
        byte[] alicePub = NostrSchnorr.GetPublicKeyX(alicePriv);

        byte[] bobPriv = NostrSchnorr.CreatePrivateKey();
        byte[] bobPub = NostrSchnorr.GetPublicKeyX(bobPriv);

        byte[] aliceSecret = NostrSchnorr.ComputeSharedSecret(alicePriv, bobPub);
        byte[] bobSecret = NostrSchnorr.ComputeSharedSecret(bobPriv, alicePub);

        if (!Convert.ToHexString(aliceSecret).Equals(Convert.ToHexString(bobSecret), StringComparison.Ordinal))
            throw new Exception("ECDH shared secret mismatch between Alice and Bob.");

        Console.WriteLine("PASSED");

        Console.Write("[TEST 1B] NIP-44 v2 official encryption vector... ");
        byte[] vectorPriv1 = Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000001");
        byte[] vectorPriv2 = Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000002");
        byte[] vectorPub2 = NostrSchnorr.GetPublicKeyX(vectorPriv2);
        byte[] vectorNonce = Convert.FromHexString("0000000000000000000000000000000000000000000000000000000000000001");
        const string expectedVector = "AgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABee0G5VSK0/9YypIObAtDKfYEAjD35uVkHyB0F4DwrcNaCXlCWZKaArsGrY6M9wnuTMxWfp1RTN9Xga8no+kF5Vsb";
        string vectorPayload = Nip44.Encrypt(vectorPriv1, vectorPub2, "a", vectorNonce);
        if (!string.Equals(vectorPayload, expectedVector, StringComparison.Ordinal))
            throw new Exception("NIP-44 official encryption vector mismatch.");
        Console.WriteLine("PASSED");

        // Test 2: NostrMailbox End-to-End Encryption & Decryption
        Console.Write("[TEST 2] Encrypted Direct Message sealing & unsealing... ");
        string tempStateA = Path.Combine(Path.GetTempPath(), $"mailbox_a_{Guid.NewGuid():N}.state");
        string tempStateB = Path.Combine(Path.GetTempPath(), $"mailbox_b_{Guid.NewGuid():N}.state");

        try
        {
            var mailboxAlice = new NostrMailbox(alicePriv, tempStateA);
            var mailboxBob = new NostrMailbox(bobPriv, tempStateB);

            string originalMessage = "Hello Bob, this message arrived while you were completely offline!";
            string bobPubHex = Convert.ToHexString(bobPub).ToLowerInvariant();
            string alicePubHex = Convert.ToHexString(alicePub).ToLowerInvariant();

            // Alice encrypts for Bob
            string encryptedBase64 = mailboxAlice.EncryptMessage(bobPub, originalMessage);
            if (string.IsNullOrEmpty(encryptedBase64))
                throw new Exception("Encryption returned empty string.");

            // Bob decrypts from Alice
            bool decryptedOk = mailboxBob.TryDecryptMessage(alicePub, encryptedBase64, out string? decryptedText);
            if (!decryptedOk || decryptedText != originalMessage)
                throw new Exception($"Decryption failed or plaintext mismatch. Got: {decryptedText}");

            // Eve (unrelated third party) attempts to decrypt
            byte[] evePriv = NostrSchnorr.CreatePrivateKey();
            var mailboxEve = new NostrMailbox(evePriv);
            bool eveDecrypted = mailboxEve.TryDecryptMessage(alicePub, encryptedBase64, out _);
            if (eveDecrypted)
                throw new Exception("Unauthorized party (Eve) was able to decrypt the private message!");

            Console.WriteLine("PASSED");

            // Test 3: Wire Event Signature & Verification
            Console.Write("[TEST 3] BIP-340 Schnorr signature on Nostr DM events... ");
            long createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string[][] tags = { new[] { "p", bobPubHex } };
            byte[] serialized = NostrDiscovery.SerializeForId(alicePubHex, createdAt, NostrMailbox.DirectMessageKind, tags, encryptedBase64);
            byte[] eventId = SHA256.HashData(serialized);

            byte[] sig = NostrSchnorr.Sign(eventId, alicePriv);
            bool sigValid = NostrSchnorr.Verify(eventId, alicePub, sig);
            if (!sigValid)
                throw new Exception("BIP-340 signature verification failed on DM event.");

            Console.WriteLine("PASSED");

            // Test 4: Global Chat message event creation, signature & hashtag tags
            Console.Write("[TEST 4] Nostr Global Chat message event sealing & verification... ");
            string globalText = "Hello everyone in the QuicPunch global network!";
            string[][] globalTags =
            {
                new[] { "t", NostrMailbox.GlobalTopic },
                new[] { "name", "Alice" },
                new[] { "client", "QuicPunch" }
            };
            byte[] serializedGlobal = NostrDiscovery.SerializeForId(alicePubHex, createdAt, NostrMailbox.GlobalMessageKind, globalTags, globalText);
            byte[] globalEventId = SHA256.HashData(serializedGlobal);
            byte[] globalSig = NostrSchnorr.Sign(globalEventId, alicePriv);
            bool globalSigValid = NostrSchnorr.Verify(globalEventId, alicePub, globalSig);
            if (!globalSigValid)
                throw new Exception("Global chat Schnorr signature verification failed.");

            Console.WriteLine("PASSED");

            // Test 5: Structured JSON Direct Message payload container
            Console.Write("[TEST 5] Structured JSON Direct Message payload handling... ");
            string structuredJson = JsonSerializer.Serialize(new
            {
                type = "dm",
                sender = "Alice",
                msgId = Guid.NewGuid().ToString(),
                content = "Offline secret message",
                timestamp = createdAt
            });
            string encStructured = mailboxAlice.EncryptMessage(bobPub, structuredJson);
            bool decStructured = mailboxBob.TryDecryptMessage(alicePub, encStructured, out string? plainStructured);
            if (!decStructured || plainStructured == null)
                throw new Exception("Failed to decrypt structured payload.");

            using var doc = JsonDocument.Parse(plainStructured);
            if (!doc.RootElement.TryGetProperty("content", out var cEl) || cEl.GetString() != "Offline secret message" ||
                !doc.RootElement.TryGetProperty("sender", out var sEl) || sEl.GetString() != "Alice")
            {
                throw new Exception("Structured JSON DM metadata extraction failed.");
            }

            Console.WriteLine("PASSED");

            // Test 6: Dual timestamp state persistence and restoration
            Console.Write("[TEST 6] Dual timestamp state persistence & restoration... ");
            string testStateFile = Path.Combine(Path.GetTempPath(), $"mailbox_state_{Guid.NewGuid():N}.json");
            try
            {
                long testDmTs = 1770000000;
                long testGlobalTs = 1770000500;
                File.WriteAllText(testStateFile, JsonSerializer.Serialize(new
                {
                    lastDmSync = testDmTs,
                    lastGlobalSync = testGlobalTs
                }));

                var restoredMailbox = new NostrMailbox(alicePriv, testStateFile);
                if (restoredMailbox.LastSyncTimestamp != testDmTs || restoredMailbox.LastGlobalSyncTimestamp != testGlobalTs)
                    throw new Exception($"State restoration mismatch. DM: {restoredMailbox.LastSyncTimestamp} vs {testDmTs}, Global: {restoredMailbox.LastGlobalSyncTimestamp} vs {testGlobalTs}");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { if (File.Exists(testStateFile)) File.Delete(testStateFile); } catch { }
            }

            // Test 7: Protocol multiplexing in offline DMs and Global Chat
            Console.Write("[TEST 7] Protocol multiplexing in offline DMs and Global Chat... ");
            Guid customProtocolA = Guid.NewGuid();
            byte[] binaryData = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 };

            var dmEnvelopeA = new Dictionary<string, object>
            {
                ["type"] = "dm",
                ["proto"] = customProtocolA.ToString(),
                ["sender"] = "Alice",
                ["msgId"] = Guid.NewGuid().ToString(),
                ["bin"] = Convert.ToBase64String(binaryData),
                ["timestamp"] = createdAt
            };
            string encPayloadA = mailboxAlice.EncryptMessage(bobPub, JsonSerializer.Serialize(dmEnvelopeA));

            long nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string[][] tagsA = { new[] { "p", bobPubHex }, new[] { "t", "quicpunch-dm" } };
            byte[] serA = NostrDiscovery.SerializeForId(alicePubHex, nowTs, NostrMailbox.DirectMessageKind, tagsA, encPayloadA);
            byte[] evIdBytesA = SHA256.HashData(serA);
            string evIdA = Convert.ToHexString(evIdBytesA).ToLowerInvariant();
            string sigA = Convert.ToHexString(NostrSchnorr.Sign(evIdBytesA, alicePriv)).ToLowerInvariant();

            var wireEvtA = new
            {
                id = evIdA,
                pubkey = alicePubHex,
                created_at = nowTs,
                kind = NostrMailbox.DirectMessageKind,
                tags = tagsA,
                content = encPayloadA,
                sig = sigA
            };

            Guid receivedProtoA = Guid.Empty;
            byte[]? receivedPayloadA = null;
            mailboxBob.OnMessageReceived += (senderPubKey, senderName, protocolId, payload, message, timestamp, msgId) =>
            {
                receivedProtoA = protocolId;
                receivedPayloadA = payload.ToArray();
            };

            string wireMsgJsonA = JsonSerializer.Serialize(new object[] { "EVENT", "sub-test-7", wireEvtA });
            mailboxBob.ProcessRelayJson(Encoding.UTF8.GetBytes(wireMsgJsonA));

            if (receivedProtoA != customProtocolA)
                throw new Exception($"Protocol ID mismatch. Expected {customProtocolA}, got {receivedProtoA}");
            if (receivedPayloadA == null || !binaryData.AsSpan().SequenceEqual(receivedPayloadA))
                throw new Exception("Binary payload mismatch after unsealing.");

            Console.WriteLine("PASSED");

            Console.Write("[TEST 7B] NIP-17/NIP-59 gift-wrap interoperability... ");
            Guid giftProtocol = Guid.NewGuid();
            string giftMessageId = Guid.CreateVersion7().ToString("D");
            byte[] giftPayload = "NIP-17 gift wrap"u8.ToArray();
            string giftEnvelope = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["v"] = 1,
                ["type"] = "dm",
                ["proto"] = giftProtocol.ToString("D"),
                ["sender"] = "Alice",
                ["msgId"] = giftMessageId,
                ["contentType"] = "application/x-quicpunch-test",
                ["bin"] = Convert.ToBase64String(giftPayload),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            var createGiftWrap = typeof(NostrMailbox).GetMethod("CreateGiftWrap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception("NIP-59 gift-wrap builder was not found.");
            object giftWrap = createGiftWrap.Invoke(mailboxAlice, new object?[]
            {
                bobPubHex,
                bobPub,
                giftEnvelope,
                DateTimeOffset.UtcNow.AddHours(1)
            }) ?? throw new Exception("NIP-59 gift-wrap builder returned null.");

            bool giftReceived = false;
            mailboxBob.OnTypedMessageReceived += (senderPubKey, senderName, protocolId, payload, contentType, message, timestamp, msgId) =>
            {
                if (protocolId == giftProtocol)
                {
                    giftReceived = senderPubKey == alicePubHex && senderName == "Alice" && msgId == giftMessageId &&
                        contentType == "application/x-quicpunch-test" && payload.Span.SequenceEqual(giftPayload);
                }
            };
            mailboxBob.ProcessRelayJson(JsonSerializer.SerializeToUtf8Bytes(new object[] { "EVENT", "sub-test-7b", giftWrap }));
            if (!giftReceived) throw new Exception("Valid NIP-17/NIP-59 message was not unwrapped and dispatched.");
            Console.WriteLine("PASSED");

            Console.Write("[TEST 7C] Maximum application payload remains relay-sized... ");
            byte[] maximumPayload = RandomNumberGenerator.GetBytes(NostrMailbox.MaxApplicationPayloadBytes);
            string maximumEnvelope = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["v"] = 1,
                ["type"] = "dm",
                ["proto"] = giftProtocol.ToString("D"),
                ["sender"] = "Alice",
                ["msgId"] = Guid.CreateVersion7().ToString("D"),
                ["contentType"] = "application/octet-stream",
                ["bin"] = Convert.ToBase64String(maximumPayload),
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            object maximumGiftWrap = createGiftWrap.Invoke(mailboxAlice, new object?[] { bobPubHex, bobPub, maximumEnvelope, null })
                ?? throw new Exception("Maximum-size gift-wrap builder returned null.");
            int maximumWireBytes = JsonSerializer.SerializeToUtf8Bytes(new object[] { "EVENT", maximumGiftWrap }).Length;
            if (maximumWireBytes > 64 * 1024)
                throw new Exception($"Maximum mailbox payload produced a {maximumWireBytes}-byte event, exceeding the 64 KiB relay target.");
            Console.WriteLine($"PASSED ({maximumWireBytes} bytes)");

            // Test 8: IMailboxChannel and IProtocolHandler integration
            Console.Write("[TEST 8] IMailboxChannel and IProtocolHandler integration... ");
            string qpDirA = Path.Combine(Path.GetTempPath(), $"qp_mb_test_a_{Guid.NewGuid():N}");
            try
            {
                using var qp = new QuicPunch.QuicPunch(appDataPath: qpDirA, listeningPort: 0) { PeerTransport = new() { AutoConnectDiscoveredPeers = false } };

                var mockHandler = new MockMailboxProtocolHandler(Guid.NewGuid());
                qp.RegisterProtocol(mockHandler);

                if (mockHandler.AttachedChannel == null)
                    throw new Exception("OnMailboxAttached was not called when registering protocol handler.");

                if (mockHandler.AttachedChannel.ProtocolId != mockHandler.ProtocolId)
                    throw new Exception("Attached channel ProtocolId mismatch.");

                var retrievedChannel = qp.GetMailboxChannel(mockHandler.ProtocolId);
                if (!ReferenceEquals(retrievedChannel, mockHandler.AttachedChannel))
                    throw new Exception("GetMailboxChannel did not return the registered channel instance.");

                long wireTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string bobNostrPub = qp.Mailbox.PublicKeyHex;
                byte[] bobNostrPubBytes = Convert.FromHexString(bobNostrPub);

                var handlerEnvelope = new Dictionary<string, object>
                {
                    ["type"] = "dm",
                    ["proto"] = mockHandler.ProtocolId.ToString(),
                    ["sender"] = "SenderAlice",
                    ["msgId"] = Guid.NewGuid().ToString(),
                    ["content"] = "Hello protocol handler!",
                    ["bin"] = Convert.ToBase64String("Hello protocol handler!"u8.ToArray()),
                    ["timestamp"] = wireTs
                };

                string encForQp = mailboxAlice.EncryptMessage(bobNostrPubBytes, JsonSerializer.Serialize(handlerEnvelope));
                string[][] qpTags = { new[] { "p", bobNostrPub }, new[] { "t", "quicpunch-dm" } };
                byte[] serQp = NostrDiscovery.SerializeForId(alicePubHex, wireTs, NostrMailbox.DirectMessageKind, qpTags, encForQp);
                byte[] idBytesQp = SHA256.HashData(serQp);
                string qpEvId = Convert.ToHexString(idBytesQp).ToLowerInvariant();
                string qpSig = Convert.ToHexString(NostrSchnorr.Sign(idBytesQp, alicePriv)).ToLowerInvariant();

                var qpEvt = new
                {
                    id = qpEvId,
                    pubkey = alicePubHex,
                    created_at = wireTs,
                    kind = NostrMailbox.DirectMessageKind,
                    tags = qpTags,
                    content = encForQp,
                    sig = qpSig
                };

                byte[] wireQpBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object[] { "EVENT", "sub-test-8", qpEvt }));
                qp.Mailbox.ProcessRelayJson(wireQpBytes);

                bool received = mockHandler.Tcs.Task.Wait(TimeSpan.FromSeconds(3));
                if (!received)
                    throw new Exception("Protocol handler OnOfflineMessageReceivedAsync was not invoked in time.");

                var (receivedPeer, receivedPayload, receivedMsgId) = mockHandler.Tcs.Task.Result;
                string receivedText = Encoding.UTF8.GetString(receivedPayload);
                if (receivedText != "Hello protocol handler!")
                    throw new Exception($"Payload mismatch: got '{receivedText}'");

                var inbox = mockHandler.AttachedChannel.GetInbox();
                if (inbox.Count == 0)
                    throw new Exception("Channel inbox was empty after receiving message.");

                if (inbox[0].ProtocolId != mockHandler.ProtocolId)
                    throw new Exception("Inbox message protocol mismatch.");

                Console.WriteLine("PASSED");
            }
            finally
            {
                try { if (Directory.Exists(qpDirA)) Directory.Delete(qpDirA, true); } catch { }
            }

            Console.Write("[TEST 9] Durable SQLite outbox, history and deduplication... ");
            string storePath = Path.Combine(Path.GetTempPath(), $"nostr_mailbox_store_{Guid.NewGuid():N}.db");
            try
            {
                await using var store = new SqliteMailboxStore(storePath, RandomNumberGenerator.GetBytes(32));
                Guid storeProtocol = Guid.NewGuid();
                string storedMessageId = Guid.CreateVersion7().ToString("D");
                var storedMessage = new MailboxStoredMessage(
                    storedMessageId,
                    storeProtocol,
                    MailboxMessageDirection.Outgoing,
                    MailboxDeliveryState.Queued,
                    null,
                    alicePubHex,
                    bobPubHex,
                    "Alice",
                    "durable"u8.ToArray(),
                    "application/x-quicpunch-test",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddHours(1),
                    false,
                    0,
                    null,
                    null,
                    null,
                    "idempotency-test");

                if (!await store.TryAddAsync(storedMessage)) throw new Exception("Could not persist queued message.");
                if (await store.TryAddAsync(storedMessage)) throw new Exception("Duplicate mailbox message was persisted.");
                await store.UpdateDeliveryAsync(storedMessageId, MailboxDeliveryState.RelayAccepted, "relay-event-id");
                var history = await store.QueryAsync(new MailboxHistoryQuery(storeProtocol));
                if (history.Count != 1 || history[0].State != MailboxDeliveryState.RelayAccepted ||
                    history[0].RelayEventId != "relay-event-id" || history[0].ContentType != "application/x-quicpunch-test")
                {
                    throw new Exception("Stored mailbox history did not preserve delivery metadata.");
                }
                Console.WriteLine("PASSED");
            }
            finally
            {
                foreach (string suffix in new[] { "", "-wal", "-shm" })
                {
                    try { if (File.Exists(storePath + suffix)) File.Delete(storePath + suffix); } catch { }
                }
            }

            Console.WriteLine("\n[✓] ALL NOSTR MAILBOX & GLOBAL CHAT TESTS PASSED SUCCESSFULLY.");
        }
        finally
        {
            try { if (File.Exists(tempStateA)) File.Delete(tempStateA); } catch { }
            try { if (File.Exists(tempStateB)) File.Delete(tempStateB); } catch { }
        }

        await Task.CompletedTask;
    }

    private sealed class MockMailboxProtocolHandler : QuicPunch.QuicPunch.IProtocolHandler
    {
        public Guid ProtocolId { get; }
        public string ProtocolName => "MockProtocol";
        public IMailboxChannel? AttachedChannel { get; private set; }
        public TaskCompletionSource<(PeerInfo Peer, byte[] Payload, string MsgId)> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MockMailboxProtocolHandler(Guid protocolId)
        {
            ProtocolId = protocolId;
        }

        public void OnMailboxAttached(IMailboxChannel channel)
        {
            AttachedChannel = channel;
        }

        public Task OnOfflineMessageReceivedAsync(PeerInfo peer, ReadOnlyMemory<byte> payload, DateTimeOffset timestamp, string messageId)
        {
            Tcs.TrySetResult((peer, payload.ToArray(), messageId));
            return Task.CompletedTask;
        }

        public Task HandleAsync(QuicPunch.QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct) => Task.CompletedTask;
        public Task DeniedAsync(PeerInfo peer, CancellationToken ct) => Task.CompletedTask;
    }
}
