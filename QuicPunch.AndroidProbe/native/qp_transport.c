#define QUIC_API_ENABLE_PREVIEW_FEATURES
#include <msquic.h>
#include <openssl/evp.h>
#include <openssl/x509.h>
#include "qp_platform.h"
#include <stdlib.h>
#include <string.h>


#include <time.h>
#include <errno.h>
#include "qp_datagram_queue.h"


#if defined(__ANDROID__)
#include <android/log.h>
#define QERROR(...) __android_log_print(ANDROID_LOG_WARN, "qp_transport", __VA_ARGS__)
#else
#include <stdio.h>
#define QERROR(...) fprintf(stderr, __VA_ARGS__), fputc('\n', stderr)
#endif
#if defined(QP_TRANSPORT_TRACE) && defined(__ANDROID__)
#include <android/log.h>
#define QLOG(...) __android_log_print(ANDROID_LOG_INFO, "qp_transport", __VA_ARGS__)
#elif defined(QP_TRANSPORT_TRACE)
#include <stdio.h>
#define QLOG(...) fprintf(stderr, __VA_ARGS__), fputc('\n', stderr)
#else
#define QLOG(...) ((void)0)
#endif

static struct timespec deadline_ms(int ms) {
    struct timespec t;
    timespec_get(&t, TIME_UTC);
    t.tv_nsec += (long)(ms % 1000) * 1000000L;
    t.tv_sec += ms / 1000 + t.tv_nsec / 1000000000L;
    t.tv_nsec %= 1000000000L;
    return t;
}

typedef struct qp_stream qp_stream;
typedef struct qp_context qp_context;
static const QUIC_API_TABLE* process_api;
// Called once by the managed bridge before opening registrations/bindings.
// This reports the requested policy, not whether an XDP driver owns the path.
QP_EXPORT int qp_initialize(int request_xdp) {
    const QUIC_API_TABLE* api = NULL;
    if (QUIC_FAILED(MsQuicOpen2(&api))) return 0;
    // Keep one API reference for the process. Closing the last reference here
    // would discard global policy before the first registration is opened.
#ifdef _WIN32
    const QUIC_API_TABLE* previous = (const QUIC_API_TABLE*)InterlockedCompareExchangePointer(
        (PVOID volatile*)&process_api, (PVOID)api, NULL);
    if (previous) { MsQuicClose(api); api = previous; }
#else
    const QUIC_API_TABLE* previous = NULL;
    if (!__atomic_compare_exchange_n(&process_api, &previous, api, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) {
        MsQuicClose(api); api = previous;
    }
#endif
    QUIC_SETTINGS settings = {0};
    settings.IsSet.XdpEnabled = TRUE;
    settings.XdpEnabled = request_xdp != 0;
    int enabled = 0;
    if (QUIC_SUCCEEDED(api->SetParam(NULL, QUIC_PARAM_GLOBAL_SETTINGS, sizeof(settings), &settings))) {
        uint32_t length = sizeof(settings);
        if (QUIC_SUCCEEDED(api->GetParam(NULL, QUIC_PARAM_GLOBAL_SETTINGS, &length, &settings)))
            enabled = settings.XdpEnabled;
    }
    return enabled;
}
typedef struct qp_chunk { struct qp_chunk* next; size_t size, offset; unsigned char data[]; } qp_chunk;
typedef struct qp_send { QUIC_BUFFER buffer; unsigned char data[]; } qp_send;

struct qp_stream {
    qp_context* owner;
    HQUIC handle;
    pthread_mutex_t mutex;
    pthread_cond_t condition;
    qp_chunk *head, *tail;
    size_t buffered_bytes;
    size_t pending_send_bytes;
    qp_stream* next;
    int eof, stopped, started, start_status, writes_closed, send_shutdown_complete, receive_paused;
    uint64_t id;
    int bidi, can_write;
};
struct qp_context {
    const QUIC_API_TABLE* api;
    HQUIC registration, configuration, listener, connection;
    pthread_mutex_t mutex;
    pthread_cond_t condition;
    qp_stream *streams_head, *streams_tail;
    qp_datagram_queue datagrams;
    size_t pending_datagram_bytes;
    // SHA3-256 of the peer's public key, the same identity desktop pins (PeerInfo.CertHash).
    unsigned char expected_hash[32];
    // DER of the certificate that passed pinning, and the handshake it belongs to.
    unsigned char* peer_cert;
    int peer_cert_length;
    HQUIC validated;
    HQUIC datagram_state_owner;
    int server, connected, stopped, error, datagram_send_enabled;
    unsigned short max_datagram_length;
    unsigned short negotiated_cipher;
    uint32_t negotiated_group;
    char negotiated_alpn[32];
};

typedef struct qp_datagram_send { QUIC_BUFFER buffer; qp_context* owner; unsigned char data[]; } qp_datagram_send;
static void release_datagram_send(void* context) {
    if (!context) return;
    qp_datagram_send* send = context;
    pthread_mutex_lock(&send->owner->mutex);
    send->owner->pending_datagram_bytes -= send->buffer.Length;
    pthread_mutex_unlock(&send->owner->mutex);
    free(send);
}

QP_EXPORT void qp_stream_close(qp_stream* s);

// Matches Sha3Crypto.Hash256(X509Certificate.GetPublicKey()): the hash covers the
// subjectPublicKey BIT STRING contents (for EC keys, the uncompressed point).
static int public_key_hash(X509* cert, unsigned char hash[32]) {
    const ASN1_BIT_STRING* key = X509_get0_pubkey_bitstr(cert);
    if (!key) return 0;
    unsigned length = 0;
    return EVP_Digest(ASN1_STRING_get0_data(key), (size_t)ASN1_STRING_length(key),
        hash, &length, EVP_sha3_256(), NULL) == 1 && length == 32;
}

QP_EXPORT int qp_public_key_hash(const unsigned char* der, unsigned der_len, unsigned char* hash) {
    if (!der || !der_len || !hash) return 0;
    const unsigned char* p = der;
    X509* cert = d2i_X509(NULL, &p, (long)der_len);
    if (!cert) return 0;
    int ok = public_key_hash(cert, hash);
    X509_free(cert);
    return ok;
}

static int is_active(qp_context* q, HQUIC h) {
    pthread_mutex_lock(&q->mutex);
    int active = q->connection == h;
    pthread_mutex_unlock(&q->mutex);
    return active;
}

static QUIC_STATUS QUIC_API discard_stream_event(HQUIC handle, void* context, QUIC_STREAM_EVENT* event) {
    qp_context* q = context;
    if (event->Type == QUIC_STREAM_EVENT_SHUTDOWN_COMPLETE) q->api->StreamClose(handle);
    return QUIC_STATUS_SUCCESS;
}

static QUIC_STATUS QUIC_API stream_event(HQUIC handle, void* context, QUIC_STREAM_EVENT* event) {
    qp_stream* s = context;
    QLOG("stream event type %d", event->Type);
    if (event->Type == QUIC_STREAM_EVENT_START_COMPLETE) {
        pthread_mutex_lock(&s->mutex);
        s->id = event->START_COMPLETE.ID;
        s->started = 1;
        s->start_status = event->START_COMPLETE.Status;
        if (QUIC_FAILED(event->START_COMPLETE.Status)) {
            s->stopped = 1;
        }
        pthread_cond_broadcast(&s->condition);
        pthread_mutex_unlock(&s->mutex);
    } else if (event->Type == QUIC_STREAM_EVENT_RECEIVE) {
        pthread_mutex_lock(&s->mutex);
        for (uint32_t i = 0; i < event->RECEIVE.BufferCount; ++i) {
            const QUIC_BUFFER* b = &event->RECEIVE.Buffers[i];
            if (!b->Length) continue;
            qp_chunk* c = malloc(sizeof(qp_chunk) + b->Length);
            if (!c) { pthread_mutex_unlock(&s->mutex); return QUIC_STATUS_OUT_OF_MEMORY; }
            c->next = NULL; c->size = b->Length; c->offset = 0;
            memcpy(c->data, b->Buffer, b->Length);
            if (s->tail) s->tail->next = c; else s->head = c;
            s->tail = c;
            s->buffered_bytes += b->Length;
        }
        int pause_receive = s->buffered_bytes >= 4 * 1024 * 1024 && !s->receive_paused;
        if (pause_receive) s->receive_paused = 1;
        if (event->RECEIVE.Flags & QUIC_RECEIVE_FLAG_FIN) s->eof = 1;
        pthread_cond_broadcast(&s->condition);
        pthread_mutex_unlock(&s->mutex);
        if (pause_receive) s->owner->api->StreamReceiveSetEnabled(handle, FALSE);
    } else if (event->Type == QUIC_STREAM_EVENT_SEND_COMPLETE) {
        qp_send* send = event->SEND_COMPLETE.ClientContext;
        if (send) {
            pthread_mutex_lock(&s->mutex);
            s->pending_send_bytes -= send->buffer.Length;
            pthread_cond_broadcast(&s->condition);
            pthread_mutex_unlock(&s->mutex);
            free(send);
        }
    } else if (event->Type == QUIC_STREAM_EVENT_PEER_SEND_SHUTDOWN ||
               event->Type == QUIC_STREAM_EVENT_PEER_SEND_ABORTED ||
               event->Type == QUIC_STREAM_EVENT_SHUTDOWN_COMPLETE) {
        pthread_mutex_lock(&s->mutex);
        s->eof = 1;
        if (event->Type == QUIC_STREAM_EVENT_SHUTDOWN_COMPLETE) s->stopped = 1;
        pthread_cond_broadcast(&s->condition);
        pthread_mutex_unlock(&s->mutex);
    } else if (event->Type == QUIC_STREAM_EVENT_SEND_SHUTDOWN_COMPLETE) {
        pthread_mutex_lock(&s->mutex);
        s->send_shutdown_complete = event->SEND_SHUTDOWN_COMPLETE.Graceful ? 1 : -1;
        pthread_cond_broadcast(&s->condition);
        pthread_mutex_unlock(&s->mutex);
    }
    (void)handle;
    return QUIC_STATUS_SUCCESS;
}

static qp_stream* new_stream(qp_context* q, HQUIC h, int bidi, int inbound) {
    qp_stream* s = calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->owner = q; s->handle = h; s->bidi = bidi; s->can_write = bidi || !inbound;
    pthread_mutex_init(&s->mutex, NULL);
    pthread_cond_init(&s->condition, NULL);
    q->api->SetCallbackHandler(h, (void*)stream_event, s);
    uint32_t len = sizeof(s->id);
    q->api->GetParam(h, QUIC_PARAM_STREAM_ID, &len, &s->id);
    return s;
}

static QUIC_STATUS QUIC_API connection_event(HQUIC h, void* context, QUIC_CONNECTION_EVENT* event) {
    qp_context* q = context;
    QLOG("connection event type %d", event->Type);
    if (event->Type == QUIC_CONNECTION_EVENT_PEER_CERTIFICATE_RECEIVED) {
        X509* cert = (X509*)event->PEER_CERTIFICATE_RECEIVED.Certificate;
        unsigned char hash[32];
        if (!cert || !public_key_hash(cert, hash)) return QUIC_STATUS_BAD_CERTIFICATE;
        unsigned diff = 0;
        for (int i = 0; i < 32; ++i) diff |= hash[i] ^ q->expected_hash[i];
        if (diff) {
            QLOG("peer certificate rejected");
            return QUIC_STATUS_BAD_CERTIFICATE;
        }
        int size = i2d_X509(cert, NULL);
        unsigned char* der = size > 0 ? malloc(size) : NULL;
        if (!der) return QUIC_STATUS_OUT_OF_MEMORY;
        unsigned char* p = der;
        i2d_X509(cert, &p);
        pthread_mutex_lock(&q->mutex);
        if (!q->connected) {
            free(q->peer_cert);
            q->peer_cert = der; der = NULL;
            q->peer_cert_length = size;
            q->validated = h;
        }
        pthread_mutex_unlock(&q->mutex);
        free(der);
    } else if (event->Type == QUIC_CONNECTION_EVENT_CONNECTED) {
        QUIC_HANDSHAKE_INFO info = {0};
        uint32_t info_length = sizeof(info);
        QUIC_STATUS tls_status = q->api->GetParam(h, QUIC_PARAM_TLS_HANDSHAKE_INFO, &info_length, &info);
        BOOLEAN datagram_enabled = FALSE;
        uint32_t datagram_size = sizeof(datagram_enabled);
        q->api->GetParam(h, QUIC_PARAM_CONN_DATAGRAM_SEND_ENABLED, &datagram_size, &datagram_enabled);
        pthread_mutex_lock(&q->mutex);
        // A server may see several handshakes on the punched port; only the
        // first one that passed pinning becomes the session.
        int accept = q->validated == h && !q->stopped && (q->connection == NULL || q->connection == h);
        if (accept) {
            q->connection = h;
            // A server's initial DATAGRAM_STATE_CHANGED can precede installation
            // of our listener callback. On a minimum-MTU path no later MTU event
            // repairs that missed notification. Query the public negotiated flag.
            // 1150 fits our minimum path MTU with worst-case QUIC/IP overhead;
            // DatagramSend still validates the peer's negotiated limit.
            if (q->datagram_state_owner != h || !q->max_datagram_length)
                q->max_datagram_length = datagram_enabled ? 1150 : 0;
            q->datagram_send_enabled = datagram_enabled;
            q->datagram_state_owner = h;
            if (QUIC_SUCCEEDED(tls_status)) {
                q->negotiated_cipher = info.CipherSuite;
                q->negotiated_group = info.TlsGroup;
            }
            unsigned alpn_length = event->CONNECTED.NegotiatedAlpnLength;
            if (alpn_length >= sizeof(q->negotiated_alpn)) alpn_length = sizeof(q->negotiated_alpn) - 1;
            if (alpn_length) memcpy(q->negotiated_alpn, event->CONNECTED.NegotiatedAlpn, alpn_length);
            q->negotiated_alpn[alpn_length] = 0;
            q->connected = 1;
        } else if (q->connection == h) {
            q->error = QUIC_STATUS_BAD_CERTIFICATE;
        }
        pthread_cond_broadcast(&q->condition);
        pthread_mutex_unlock(&q->mutex);
        // The return value of CONNECTED is ignored by MsQuic, so close explicitly.
        if (!accept) q->api->ConnectionShutdown(h, QUIC_CONNECTION_SHUTDOWN_FLAG_NONE, 0);
    } else if (!is_active(q, h)) {
        // Server-side handshake that did not become the session.
        if (event->Type == QUIC_CONNECTION_EVENT_PEER_STREAM_STARTED) {
            HQUIC stream = event->PEER_STREAM_STARTED.Stream;
            q->api->SetCallbackHandler(stream, (void*)discard_stream_event, q);
            q->api->StreamShutdown(stream, QUIC_STREAM_SHUTDOWN_FLAG_ABORT, 0);
        } else if (event->Type == QUIC_CONNECTION_EVENT_DATAGRAM_STATE_CHANGED) {
            // Transport parameters arrive before CONNECTED; keep them for the pending session.
            pthread_mutex_lock(&q->mutex);
            if (!q->connection) {
                q->datagram_state_owner = h;
                q->datagram_send_enabled = event->DATAGRAM_STATE_CHANGED.SendEnabled;
                q->max_datagram_length = event->DATAGRAM_STATE_CHANGED.MaxSendLength;
            }
            pthread_mutex_unlock(&q->mutex);
        } else if (event->Type == QUIC_CONNECTION_EVENT_DATAGRAM_SEND_STATE_CHANGED) {
            if (QUIC_DATAGRAM_SEND_STATE_IS_FINAL(event->DATAGRAM_SEND_STATE_CHANGED.State))
                release_datagram_send(event->DATAGRAM_SEND_STATE_CHANGED.ClientContext);
        } else if (event->Type == QUIC_CONNECTION_EVENT_SHUTDOWN_INITIATED_BY_TRANSPORT) {
            QLOG("rejected handshake ended: 0x%x", event->SHUTDOWN_INITIATED_BY_TRANSPORT.Status);
        } else if (event->Type == QUIC_CONNECTION_EVENT_SHUTDOWN_COMPLETE) {
            pthread_mutex_lock(&q->mutex);
            if (q->validated == h) q->validated = NULL;
            pthread_mutex_unlock(&q->mutex);
            q->api->ConnectionClose(h);
        }
    } else if (event->Type == QUIC_CONNECTION_EVENT_PEER_STREAM_STARTED) {
        QLOG("peer stream started");
        qp_stream* s = new_stream(q, event->PEER_STREAM_STARTED.Stream,
            !(event->PEER_STREAM_STARTED.Flags & QUIC_STREAM_OPEN_FLAG_UNIDIRECTIONAL), 1);
        if (!s) return QUIC_STATUS_OUT_OF_MEMORY;
        pthread_mutex_lock(&q->mutex);
        if (q->streams_tail) q->streams_tail->next = s; else q->streams_head = s;
        q->streams_tail = s;
        pthread_cond_broadcast(&q->condition);
        pthread_mutex_unlock(&q->mutex);
    } else if (event->Type == QUIC_CONNECTION_EVENT_DATAGRAM_STATE_CHANGED) {
        pthread_mutex_lock(&q->mutex);
        q->datagram_state_owner = h;
        q->datagram_send_enabled = event->DATAGRAM_STATE_CHANGED.SendEnabled;
        q->max_datagram_length = event->DATAGRAM_STATE_CHANGED.MaxSendLength;
        pthread_cond_broadcast(&q->condition);
        pthread_mutex_unlock(&q->mutex);
    } else if (event->Type == QUIC_CONNECTION_EVENT_DATAGRAM_RECEIVED) {
        const QUIC_BUFFER* b = event->DATAGRAM_RECEIVED.Buffer;
        if (b && b->Length && b->Length <= 65535) {
            pthread_mutex_lock(&q->mutex);
            if (!q->stopped && qp_datagram_enqueue(&q->datagrams, b->Buffer, b->Length, monotonic_ms()))
                pthread_cond_broadcast(&q->condition);
            pthread_mutex_unlock(&q->mutex);
        }
    } else if (event->Type == QUIC_CONNECTION_EVENT_DATAGRAM_SEND_STATE_CHANGED) {
        if (QUIC_DATAGRAM_SEND_STATE_IS_FINAL(event->DATAGRAM_SEND_STATE_CHANGED.State))
            release_datagram_send(event->DATAGRAM_SEND_STATE_CHANGED.ClientContext);
    } else if (event->Type == QUIC_CONNECTION_EVENT_SHUTDOWN_INITIATED_BY_TRANSPORT) {
        pthread_mutex_lock(&q->mutex);
        QERROR("Transport shutdown: status=0x%x, QUIC error=0x%llx",
            event->SHUTDOWN_INITIATED_BY_TRANSPORT.Status,
            (unsigned long long)event->SHUTDOWN_INITIATED_BY_TRANSPORT.ErrorCode);
        q->error = event->SHUTDOWN_INITIATED_BY_TRANSPORT.Status;
        q->stopped = 1;
        pthread_cond_broadcast(&q->condition);
        pthread_mutex_unlock(&q->mutex);
    } else if (event->Type == QUIC_CONNECTION_EVENT_SHUTDOWN_INITIATED_BY_PEER ||
               event->Type == QUIC_CONNECTION_EVENT_SHUTDOWN_COMPLETE) {
        if (event->Type == QUIC_CONNECTION_EVENT_SHUTDOWN_INITIATED_BY_PEER)
            QERROR("Peer shutdown: QUIC application error=0x%llx",
                (unsigned long long)event->SHUTDOWN_INITIATED_BY_PEER.ErrorCode);
        pthread_mutex_lock(&q->mutex);
        q->stopped = 1;
        pthread_cond_broadcast(&q->condition);
        pthread_mutex_unlock(&q->mutex);
    }
    (void)h;
    return QUIC_STATUS_SUCCESS;
}

static QUIC_STATUS QUIC_API listener_event(HQUIC h, void* context, QUIC_LISTENER_EVENT* event) {
    qp_context* q = context;
    if (event->Type == QUIC_LISTENER_EVENT_NEW_CONNECTION) {
        HQUIC conn = event->NEW_CONNECTION.Connection;
        pthread_mutex_lock(&q->mutex);
        int refuse = q->connection != NULL || q->stopped;
        pthread_mutex_unlock(&q->mutex);
        // Stray or unauthenticated handshakes no longer occupy the session slot:
        // each is pinned independently and closed on SHUTDOWN_COMPLETE if it loses.
        if (refuse) return QUIC_STATUS_CONNECTION_REFUSED;
        q->api->SetCallbackHandler(conn, (void*)connection_event, q);
        return q->api->ConnectionSetConfiguration(conn, q->configuration);
    }
    (void)h;
    return QUIC_STATUS_SUCCESS;
}

QP_EXPORT qp_context* qp_create(const unsigned char* pfx, unsigned pfx_len,
        const unsigned char* expected_hash, unsigned expected_len, int server) {
    if (!pfx || !pfx_len || !expected_hash || expected_len != sizeof(((qp_context*)0)->expected_hash)) return NULL;
    qp_context* q = calloc(1, sizeof(*q));
    if (!q) return NULL;
    pthread_mutex_init(&q->mutex, NULL);
    pthread_cond_init(&q->condition, NULL);
    memcpy(q->expected_hash, expected_hash, expected_len);
    q->server = server;
    if (QUIC_FAILED(MsQuicOpen2(&q->api))) goto fail;
    QUIC_REGISTRATION_CONFIG reg = { "quicpunch", QUIC_EXECUTION_PROFILE_LOW_LATENCY };
    if (QUIC_FAILED(q->api->RegistrationOpen(&reg, &q->registration))) goto fail;
    uint8_t a[] = "h3", b[] = "h3-29", c[] = "quic-punch";
    QUIC_BUFFER alpn[] = { { 2, a }, { 5, b }, { 10, c } };
    QUIC_SETTINGS settings = {0};
    // Match desktop before the handshake: changing the stored CC setting after
    // ConnectionStart does not replace MsQuic's active congestion controller.
    settings.IsSet.CongestionControlAlgorithm = TRUE;
    settings.CongestionControlAlgorithm = QUIC_CONGESTION_CONTROL_ALGORITHM_BBR;
    settings.IsSet.ConnFlowControlWindow = TRUE;
    settings.ConnFlowControlWindow = 16 * 1024 * 1024;
    settings.IsSet.StreamRecvWindowDefault = TRUE;
    settings.StreamRecvWindowDefault = 4 * 1024 * 1024;
    settings.IsSet.StreamRecvBufferDefault = TRUE;
    settings.StreamRecvBufferDefault = 64 * 1024;
    settings.IsSet.MigrationEnabled = TRUE; settings.MigrationEnabled = TRUE;
    settings.IsSet.GreaseQuicBitEnabled = TRUE; settings.GreaseQuicBitEnabled = TRUE;
    settings.IsSet.SendBufferingEnabled = TRUE;
    settings.SendBufferingEnabled = TRUE;
    settings.IsSet.PacingEnabled = TRUE;
    settings.PacingEnabled = TRUE;
    settings.IsSet.MaxAckDelayMs = TRUE;
    settings.MaxAckDelayMs = 10;
    settings.IsSet.PeerBidiStreamCount = TRUE;
    settings.PeerBidiStreamCount = 512;
    settings.IsSet.PeerUnidiStreamCount = TRUE;
    settings.PeerUnidiStreamCount = 512;
    settings.IsSet.DatagramReceiveEnabled = TRUE;
    settings.DatagramReceiveEnabled = TRUE;
    settings.IsSet.MinimumMtu = TRUE; settings.MinimumMtu = 1248;
    settings.IsSet.MaximumMtu = TRUE; settings.MaximumMtu = 1500;
    settings.IsSet.EcnEnabled = TRUE; settings.EcnEnabled = TRUE;
    settings.IsSet.KeepAliveIntervalMs = TRUE;
    settings.KeepAliveIntervalMs = 19000;
    // Same as desktop (QuicPunchConnection: IdleTimeout = 10 min). Without it MsQuic's
    // 30 s default wins the negotiation and sessions drop whenever keep-alives stall.
    settings.IsSet.IdleTimeoutMs = TRUE;
    settings.IdleTimeoutMs = 10 * 60 * 1000;
    if (QUIC_FAILED(q->api->ConfigurationOpen(q->registration, alpn, 3, &settings, sizeof(settings), NULL, &q->configuration))) goto fail;
    QUIC_CERTIFICATE_PKCS12 pkcs12 = { pfx, pfx_len, NULL };
    QUIC_CREDENTIAL_CONFIG credential = {0};
    credential.Type = QUIC_CREDENTIAL_TYPE_CERTIFICATE_PKCS12;
    credential.CertificatePkcs12 = &pkcs12;
    credential.Flags = QUIC_CREDENTIAL_FLAG_NO_CERTIFICATE_VALIDATION |
        QUIC_CREDENTIAL_FLAG_INDICATE_CERTIFICATE_RECEIVED |
        (server ? QUIC_CREDENTIAL_FLAG_REQUIRE_CLIENT_AUTHENTICATION : QUIC_CREDENTIAL_FLAG_CLIENT);
    if (QUIC_FAILED(q->api->ConfigurationLoadCredential(q->configuration, &credential))) goto fail;
    return q;
fail:
    if (q->configuration) q->api->ConfigurationClose(q->configuration);
    if (q->registration) q->api->RegistrationClose(q->registration);
    if (q->api) MsQuicClose(q->api);
    pthread_cond_destroy(&q->condition);
    pthread_mutex_destroy(&q->mutex);
    free(q);
    return NULL;
}

// Stable bridge ABI: QUIC_SETTINGS layout stays on the native side, compiled
// against the pinned public header. Never inspect System.Net.Quic objects.
QP_EXPORT int qp_configure(qp_context* q, const uint64_t* v, int count) {
    if (!q || !v || count != 13 || q->connection || q->listener || v[12] > 1 ||
        v[0] < 1248 || v[1] < v[0] || v[1] > 65527 || !v[2] || !v[3]) return 0;
    QUIC_SETTINGS s = {0};
    s.IsSet.CongestionControlAlgorithm = TRUE; s.CongestionControlAlgorithm = (uint16_t)v[12];
    s.IsSet.MinimumMtu = TRUE; s.MinimumMtu = (uint16_t)v[0];
    s.IsSet.MaximumMtu = TRUE; s.MaximumMtu = (uint16_t)v[1];
    s.IsSet.ConnFlowControlWindow = TRUE; s.ConnFlowControlWindow = (uint32_t)v[2];
    s.IsSet.StreamRecvWindowDefault = TRUE; s.StreamRecvWindowDefault = (uint32_t)v[3];
    s.IsSet.KeepAliveIntervalMs = TRUE; s.KeepAliveIntervalMs = (uint32_t)v[4];
    s.IsSet.MtuDiscoverySearchCompleteTimeoutUs = TRUE; s.MtuDiscoverySearchCompleteTimeoutUs = v[5];
    s.IsSet.MtuDiscoveryMissingProbeCount = TRUE; s.MtuDiscoveryMissingProbeCount = (uint8_t)v[6];
    s.IsSet.InitialWindowPackets = TRUE; s.InitialWindowPackets = (uint32_t)v[7];
    // Application authentication is replay-sensitive. Do not enable 0-RTT.
    if (v[8]) return 0;
    s.IsSet.PacingEnabled = TRUE; s.PacingEnabled = v[9] != 0;
    s.IsSet.EcnEnabled = TRUE; s.EcnEnabled = v[10] != 0;
    s.IsSet.HyStartEnabled = TRUE; s.HyStartEnabled = v[11] != 0;
    return QUIC_SUCCEEDED(q->api->SetParam(q->configuration, QUIC_PARAM_CONFIGURATION_SETTINGS, sizeof(s), &s));
}

QP_EXPORT int qp_get_param(qp_context* q, uint32_t param, uint32_t* length, void* value) {
    return q && q->connection && QUIC_SUCCEEDED(q->api->GetParam(q->connection, param, length, value));
}
QP_EXPORT int qp_set_param(qp_context* q, uint32_t param, uint32_t length, const void* value) {
    return q && q->connection && QUIC_SUCCEEDED(q->api->SetParam(q->connection, param, length, value));
}
QP_EXPORT int qp_global_get_param(uint32_t param, uint32_t* length, void* value) {
    const QUIC_API_TABLE* api = NULL;
    if (QUIC_FAILED(MsQuicOpen2(&api))) return 0;
    int result = QUIC_SUCCEEDED(api->GetParam(NULL, param, length, value));
    MsQuicClose(api); return result;
}
QP_EXPORT int qp_tune(qp_context* q, int option, uint64_t a, uint64_t b, uint64_t c, uint64_t d) {
    if (!q || !q->connection) return 0;
    QUIC_SETTINGS s = {0};
    if (option == 0) { s.IsSet.KeepAliveIntervalMs = TRUE; s.KeepAliveIntervalMs = (uint32_t)a; }
    else if (option == 1) {
        if (!a || !b || a > UINT32_MAX || b > UINT32_MAX) return 0;
        s.IsSet.ConnFlowControlWindow = TRUE; s.ConnFlowControlWindow = (uint32_t)a;
        s.IsSet.StreamRecvWindowDefault = TRUE; s.StreamRecvWindowDefault = (uint32_t)b;
    } else if (option == 2) {
        QUIC_SETTINGS current = {0}; uint32_t size = sizeof(current);
        if (QUIC_FAILED(q->api->GetParam(q->connection, QUIC_PARAM_CONN_SETTINGS, &size, &current)) ||
            current.MinimumMtu != a || current.MaximumMtu != b || !d || d > 255) return 0;
        s.IsSet.MtuDiscoverySearchCompleteTimeoutUs = TRUE; s.MtuDiscoverySearchCompleteTimeoutUs = c;
        s.IsSet.MtuDiscoveryMissingProbeCount = TRUE; s.MtuDiscoveryMissingProbeCount = (uint8_t)d;
    } else return 0;
    return QUIC_SUCCEEDED(q->api->SetParam(q->connection, QUIC_PARAM_CONN_SETTINGS, sizeof(s), &s));
}

QP_EXPORT int qp_listen(qp_context* q, unsigned short port) {
    if (!q) return -1;
    if (!q->listener && QUIC_FAILED(q->api->ListenerOpen(q->registration, listener_event, q, &q->listener))) return -2;
    uint8_t a[] = "h3", b[] = "h3-29", c[] = "quic-punch";
    QUIC_BUFFER alpn[] = { { 2, a }, { 5, b }, { 10, c } };
    QUIC_ADDR addr = {0};
    // MsQuic listener matching uses AF_UNSPEC for both families. AF_INET6
    // alone opens a dual UDP binding but rejects IPv4 clients with ALPN failure.
    addr.Ipv6.sin6_family = AF_UNSPEC; addr.Ipv6.sin6_port = htons(port);
    QUIC_STATUS status = q->api->ListenerStart(q->listener, alpn, 3, &addr);
    if (QUIC_FAILED(status)) {
        q->api->ListenerClose(q->listener);
        q->listener = NULL;
        return (int)status;
    }
    return 0;
}

// host must be a literal IP address. server_name is only sent as SNI, matching the
// desktop client's disguise host; NULL or empty falls back to the address.
QP_EXPORT int qp_connect(qp_context* q, const char* host, unsigned short port,
        unsigned short local_port, const char* server_name) {
    if (!q || !host || q->connection) return -1;
    QUIC_ADDR remote = {0};
    int family;
    if (inet_pton(AF_INET, host, &remote.Ipv4.sin_addr) == 1) {
        family = AF_INET;
        remote.Ipv4.sin_family = AF_INET; remote.Ipv4.sin_port = htons(port);
    } else if (inet_pton(AF_INET6, host, &remote.Ipv6.sin6_addr) == 1) {
        family = AF_INET6;
        remote.Ipv6.sin6_family = AF_INET6; remote.Ipv6.sin6_port = htons(port);
    } else {
        return -1;
    }
    QUIC_STATUS status = q->api->ConnectionOpen(q->registration, connection_event, q, &q->connection);
    if (QUIC_FAILED(status)) { q->connection = NULL; return (int)status; }
    status = q->api->SetParam(q->connection, QUIC_PARAM_CONN_REMOTE_ADDRESS, sizeof(remote), &remote);
    if (QUIC_FAILED(status)) return (int)status;
    if (local_port) {
        QUIC_ADDR local = {0};
        if (family == AF_INET) { local.Ipv4.sin_family = AF_INET; local.Ipv4.sin_port = htons(local_port); }
        else { local.Ipv6.sin6_family = AF_INET6; local.Ipv6.sin6_port = htons(local_port); }
        status = q->api->SetParam(q->connection, QUIC_PARAM_CONN_LOCAL_ADDRESS, sizeof(local), &local);
        if (QUIC_FAILED(status)) return (int)status;
    }
    // With the remote address already set, MsQuic uses the name only for SNI.
    const char* sni = server_name && *server_name ? server_name : host;
    status = q->api->ConnectionStart(q->connection, q->configuration,
        family == AF_INET ? QUIC_ADDRESS_FAMILY_INET : QUIC_ADDRESS_FAMILY_INET6, sni, port);
    return QUIC_FAILED(status) ? (int)status : 0;
}

// Copies the pinned peer certificate (DER). Returns its length, the negated
// required length when capacity is too small, or 0 before a session exists.
QP_EXPORT int qp_get_peer_certificate(qp_context* q, unsigned char* output, int capacity) {
    if (!q) return 0;
    pthread_mutex_lock(&q->mutex);
    int length = q->connected && q->peer_cert ? q->peer_cert_length : 0;
    if (length > 0) {
        if (output && capacity >= length) memcpy(output, q->peer_cert, length);
        else length = -length;
    }
    pthread_mutex_unlock(&q->mutex);
    return length;
}

QP_EXPORT int qp_wait_connected(qp_context* q, int timeout_ms) {
    if (!q) return -1;
    pthread_mutex_lock(&q->mutex);
    struct timespec deadline = deadline_ms(timeout_ms);
    while (!q->connected && !q->stopped && pthread_cond_timedwait(&q->condition, &q->mutex, &deadline) != ETIMEDOUT) {}
    int result = q->connected ? 0 : (q->stopped ? (q->error ? q->error : -2) : -3);
    pthread_mutex_unlock(&q->mutex);
    return result;
}

QP_EXPORT qp_stream* qp_open_stream(qp_context* q, int bidi) {
    if (!q || !q->connection || !q->connected) return NULL;
    HQUIC h = NULL;
    QUIC_STATUS status = q->api->StreamOpen(q->connection,
        bidi ? QUIC_STREAM_OPEN_FLAG_NONE : QUIC_STREAM_OPEN_FLAG_UNIDIRECTIONAL,
        stream_event, NULL, &h);
    if (QUIC_FAILED(status)) return NULL;
    qp_stream* s = new_stream(q, h, bidi, 0);
    if (!s) { q->api->StreamClose(h); return NULL; }
    status = q->api->StreamStart(h, QUIC_STREAM_START_FLAG_FAIL_BLOCKED);
    if (QUIC_FAILED(status)) { qp_stream_close(s); return NULL; }
    return s;
}

QP_EXPORT int qp_stream_start_status(qp_stream* s) {
    if (!s) return -2;
    pthread_mutex_lock(&s->mutex);
    int result = s->stopped ? -2 : !s->started ? -3 : QUIC_FAILED(s->start_status) ? -2 : 0;
    pthread_mutex_unlock(&s->mutex);
    return result;
}

QP_EXPORT qp_stream* qp_accept_stream(qp_context* q, int timeout_ms) {
    if (!q) return NULL;
    pthread_mutex_lock(&q->mutex);
    struct timespec deadline = deadline_ms(timeout_ms);
    while (!q->streams_head && !q->stopped && pthread_cond_timedwait(&q->condition, &q->mutex, &deadline) != ETIMEDOUT) {}
    qp_stream* s = q->streams_head;
    if (s) {
        q->streams_head = s->next;
        if (!q->streams_head) q->streams_tail = NULL;
        s->next = NULL;
    }
    pthread_mutex_unlock(&q->mutex);
    return s;
}

QP_EXPORT int qp_stream_read(qp_stream* s, unsigned char* data, int capacity, int timeout_ms) {
    if (!s || !data || capacity <= 0) return -1;
    pthread_mutex_lock(&s->mutex);
    struct timespec deadline = deadline_ms(timeout_ms);
    while (!s->head && !s->eof && !s->stopped && pthread_cond_timedwait(&s->condition, &s->mutex, &deadline) != ETIMEDOUT) {}
    int result = (!s->head && !s->eof && !s->stopped) ? -3 : 0;
    if (s->head) {
        qp_chunk* c = s->head;
        result = (int)((c->size - c->offset) < (size_t)capacity ? c->size - c->offset : (size_t)capacity);
        memcpy(data, c->data + c->offset, result);
        c->offset += result;
        s->buffered_bytes -= result;
        if (c->offset == c->size) {
            s->head = c->next;
            if (!s->head) s->tail = NULL;
            free(c);
        }
    }
    int resume_receive = s->receive_paused && s->buffered_bytes < 2 * 1024 * 1024;
    if (resume_receive) s->receive_paused = 0;
    pthread_mutex_unlock(&s->mutex);
    if (resume_receive) s->owner->api->StreamReceiveSetEnabled(s->handle, TRUE);
    return result;
}

QP_EXPORT int qp_stream_write(qp_stream* s, const unsigned char* data, int length) {
    if (!s || !data || length < 0) return -1;
    if (!s->can_write) return -2;
    pthread_mutex_lock(&s->mutex);
    if (s->stopped || s->writes_closed) { pthread_mutex_unlock(&s->mutex); return -2; }
    if (s->pending_send_bytes >= 4 * 1024 * 1024) { pthread_mutex_unlock(&s->mutex); return -3; }
    s->pending_send_bytes += length;
    pthread_mutex_unlock(&s->mutex);
    qp_send* send = malloc(sizeof(qp_send) + length);
    if (!send) {
        pthread_mutex_lock(&s->mutex);
        s->pending_send_bytes -= length;
        pthread_mutex_unlock(&s->mutex);
        return -4;
    }
    send->buffer.Buffer = send->data;
    send->buffer.Length = length;
    memcpy(send->data, data, length);
    QUIC_STATUS status = s->owner->api->StreamSend(s->handle, &send->buffer, 1, QUIC_SEND_FLAG_NONE, send);
    QLOG("stream send status 0x%x length %d", status, length);
    if (QUIC_FAILED(status)) {
        pthread_mutex_lock(&s->mutex);
        s->pending_send_bytes -= length;
        pthread_mutex_unlock(&s->mutex);
        free(send);
        return (int)status;
    }
    return length;
}

QP_EXPORT void qp_stream_complete(qp_stream* s) {
    if (!s) return;
    pthread_mutex_lock(&s->mutex);
    int complete = s->can_write && !s->writes_closed && !s->stopped;
    if (complete) s->writes_closed = 1;
    pthread_mutex_unlock(&s->mutex);
    if (complete) s->owner->api->StreamShutdown(s->handle, QUIC_STREAM_SHUTDOWN_FLAG_GRACEFUL, 0);
}

QP_EXPORT int qp_stream_writes_closed(qp_stream* s) {
    if (!s) return -1;
    pthread_mutex_lock(&s->mutex);
    int result = s->send_shutdown_complete == 1 ? 1 : (s->stopped || s->send_shutdown_complete < 0 ? -1 : 0);
    pthread_mutex_unlock(&s->mutex);
    return result;
}

QP_EXPORT void qp_stream_abort(qp_stream* s, uint64_t error) {
    if (!s) return;
    pthread_mutex_lock(&s->mutex);
    s->stopped = 1;
    pthread_cond_broadcast(&s->condition);
    pthread_mutex_unlock(&s->mutex);
    s->owner->api->StreamShutdown(s->handle,
        s->can_write ? QUIC_STREAM_SHUTDOWN_FLAG_ABORT : QUIC_STREAM_SHUTDOWN_FLAG_ABORT_RECEIVE, error);
}

QP_EXPORT int qp_stream_flush(qp_stream* s, int timeout_ms) {
    if (!s) return -1;
    pthread_mutex_lock(&s->mutex);
    struct timespec deadline = deadline_ms(timeout_ms);
    while (s->pending_send_bytes && !s->stopped &&
           pthread_cond_timedwait(&s->condition, &s->mutex, &deadline) != ETIMEDOUT) {}
    int result = s->pending_send_bytes == 0 ? 0 : (s->stopped ? -2 : -3);
    pthread_mutex_unlock(&s->mutex);
    return result;
}

QP_EXPORT void qp_stream_close(qp_stream* s) {
    if (!s) return;
    struct timespec deadline = deadline_ms(1000);
    pthread_mutex_lock(&s->mutex);
    while (s->pending_send_bytes && !s->stopped &&
           pthread_cond_timedwait(&s->condition, &s->mutex, &deadline) != ETIMEDOUT) {}
    int graceful = s->can_write && !s->stopped && s->pending_send_bytes == 0;
    s->stopped = 1;
    pthread_cond_broadcast(&s->condition);
    pthread_mutex_unlock(&s->mutex);
    if (graceful) {
        s->owner->api->StreamShutdown(s->handle, QUIC_STREAM_SHUTDOWN_FLAG_GRACEFUL, 0);
        deadline = deadline_ms(500);
        pthread_mutex_lock(&s->mutex);
        while (!s->send_shutdown_complete &&
               pthread_cond_timedwait(&s->condition, &s->mutex, &deadline) != ETIMEDOUT) {}
        pthread_mutex_unlock(&s->mutex);
        s->owner->api->StreamShutdown(s->handle, QUIC_STREAM_SHUTDOWN_FLAG_ABORT_RECEIVE, 0);
    } else if (s->can_write) {
        s->owner->api->StreamShutdown(s->handle,
            QUIC_STREAM_SHUTDOWN_FLAG_ABORT_SEND | QUIC_STREAM_SHUTDOWN_FLAG_ABORT_RECEIVE, 0);
    } else {
        s->owner->api->StreamShutdown(s->handle, QUIC_STREAM_SHUTDOWN_FLAG_ABORT_RECEIVE, 0);
    }
    s->owner->api->StreamClose(s->handle);
    qp_chunk* c = s->head;
    while (c) { qp_chunk* next = c->next; free(c); c = next; }
    pthread_cond_destroy(&s->condition);
    pthread_mutex_destroy(&s->mutex);
    free(s);
}

QP_EXPORT uint64_t qp_stream_id(qp_stream* s) {
    if (!s) return 0;
    uint64_t id = s->id;
    uint32_t length = sizeof(id);
    if (!QUIC_FAILED(s->owner->api->GetParam(s->handle, QUIC_PARAM_STREAM_ID, &length, &id)))
        s->id = id;
    return s->id;
}
QP_EXPORT int qp_stream_bidi(qp_stream* s) { return s ? s->bidi : 0; }
QP_EXPORT int qp_stream_set_priority(qp_stream* s, unsigned short priority) {
    if (!s) return 0;
    return !QUIC_FAILED(s->owner->api->SetParam(s->handle, QUIC_PARAM_STREAM_PRIORITY, sizeof(priority), &priority));
}
QP_EXPORT unsigned short qp_stream_get_priority(qp_stream* s) {
    if (!s) return 0;
    unsigned short priority = 0x7fff;
    uint32_t length = sizeof(priority);
    s->owner->api->GetParam(s->handle, QUIC_PARAM_STREAM_PRIORITY, &length, &priority);
    return priority;
}
QP_EXPORT int qp_stream_get_stats(qp_stream* s, uint32_t* length, void* output) {
    return s && QUIC_SUCCEEDED(s->owner->api->GetParam(s->handle, QUIC_PARAM_STREAM_STATISTICS, length, output));
}
QP_EXPORT int qp_is_stopped(qp_context* q) { if (!q) return 1; pthread_mutex_lock(&q->mutex); int stopped = q->stopped; pthread_mutex_unlock(&q->mutex); return stopped; }

QP_EXPORT int qp_send_datagram(qp_context* q, const unsigned char* data, int length) {
    if (!q || !q->connection || !data || length <= 0 || length > 65535) return -1;
    pthread_mutex_lock(&q->mutex);
    int allowed = !q->stopped && q->datagram_send_enabled && length <= q->max_datagram_length &&
        q->pending_datagram_bytes + (size_t)length <= QP_DATAGRAM_MAX_BYTES;
    if (allowed) q->pending_datagram_bytes += (size_t)length;
    pthread_mutex_unlock(&q->mutex);
    if (!allowed) return -2;
    qp_datagram_send* send = malloc(sizeof(*send) + length);
    if (!send) {
        pthread_mutex_lock(&q->mutex); q->pending_datagram_bytes -= (size_t)length; pthread_mutex_unlock(&q->mutex);
        return -3;
    }
    send->owner = q; send->buffer.Buffer = send->data; send->buffer.Length = length;
    memcpy(send->data, data, length);
    QUIC_STATUS status = q->api->DatagramSend(q->connection, &send->buffer, 1,
        QUIC_SEND_FLAG_DGRAM_PRIORITY | QUIC_SEND_FLAG_CANCEL_ON_BLOCKED, send);
    if (QUIC_FAILED(status)) { release_datagram_send(send); return (int)status; }
    return length;
}

QP_EXPORT int qp_receive_datagram(qp_context* q, unsigned char* data, int capacity, int timeout_ms) {
    if (!q || !data || capacity <= 0) return -1;
    pthread_mutex_lock(&q->mutex);
    struct timespec deadline = deadline_ms(timeout_ms);
    while (!q->datagrams.head && !q->stopped && pthread_cond_timedwait(&q->condition, &q->mutex, &deadline) != ETIMEDOUT) {}
    int result = q->stopped ? -2 : -3;
    if (!q->stopped) result = qp_datagram_dequeue(&q->datagrams, data, (size_t)capacity, monotonic_ms());
    pthread_mutex_unlock(&q->mutex);
    return result;
}

QP_EXPORT int qp_datagram_state(qp_context* q) {
    if (!q) return 0;
    pthread_mutex_lock(&q->mutex);
    int state = q->datagram_send_enabled ? q->max_datagram_length : 0;
    pthread_mutex_unlock(&q->mutex);
    return state;
}

QP_EXPORT int qp_get_address(qp_context* q, int remote,
        char* output, int capacity, unsigned short* port) {
    if (!q || !q->connection || !output || capacity < 16 || !port) return 0;
    QUIC_ADDR addr = {0};
    uint32_t length = sizeof(addr);
    QUIC_STATUS status = q->api->GetParam(q->connection,
        remote ? QUIC_PARAM_CONN_REMOTE_ADDRESS : QUIC_PARAM_CONN_LOCAL_ADDRESS,
        &length, &addr);
    if (QUIC_FAILED(status)) return 0;
    const void* ip = NULL;
    if (QuicAddrGetFamily(&addr) == AF_INET) {
        ip = &addr.Ipv4.sin_addr;
        *port = ntohs(addr.Ipv4.sin_port);
    } else if (QuicAddrGetFamily(&addr) == AF_INET6) {
        ip = &addr.Ipv6.sin6_addr;
        *port = ntohs(addr.Ipv6.sin6_port);
    } else return 0;
    return inet_ntop(QuicAddrGetFamily(&addr), ip, output, capacity) != NULL;
}

QP_EXPORT int qp_get_stats(qp_context* q, uint64_t* output, int capacity) {
    if (!q || !q->connection || !output || capacity < 20) return 0;
    QUIC_STATISTICS_V2 stats = {0};
    uint32_t length = sizeof(stats);
    if (QUIC_FAILED(q->api->GetParam(q->connection, QUIC_PARAM_CONN_STATISTICS_V2, &length, &stats))) return 0;
    output[0] = stats.Rtt;
    output[1] = stats.MinRtt;
    output[2] = stats.MaxRtt;
    output[3] = stats.RttVariance;
    output[4] = stats.SendPathMtu;
    output[5] = stats.SendTotalPackets;
    output[6] = stats.SendSuspectedLostPackets;
    output[7] = stats.SendSpuriousLostPackets;
    output[8] = stats.SendTotalBytes;
    output[9] = stats.RecvTotalPackets;
    output[10] = stats.RecvTotalBytes;
    output[11] = stats.RecvDroppedPackets;
    output[12] = stats.RecvDecryptionFailures;
    output[13] = stats.SendCongestionWindow;
    output[14] = stats.SendCongestionCount;
    output[15] = stats.RecvValidAckFrames;
    output[16] = stats.RecvReorderedPackets;
    output[17] = stats.RecvDuplicatePackets;
    output[18] = stats.SendTotalStreamBytes;
    output[19] = stats.RecvTotalStreamBytes;
    if (capacity > 20) {
        QUIC_SETTINGS settings = {0};
        uint32_t settings_length = sizeof(settings);
        output[20] = QUIC_SUCCEEDED(q->api->GetParam(q->connection, QUIC_PARAM_CONN_SETTINGS,
            &settings_length, &settings)) ? settings.CongestionControlAlgorithm : UINT64_MAX;
    }
    return 1;
}

QP_EXPORT int qp_get_tls(qp_context* q, char* alpn, int capacity, unsigned short* cipher) {
    if (!q || !q->connection || !alpn || capacity < 32 || !cipher) return 0;
    pthread_mutex_lock(&q->mutex);
    memcpy(alpn, q->negotiated_alpn, sizeof(q->negotiated_alpn));
    *cipher = q->negotiated_cipher;
    pthread_mutex_unlock(&q->mutex);
    return *cipher != 0;
}

QP_EXPORT uint32_t qp_get_tls_group(qp_context* q) {
    if (!q) return 0;
    pthread_mutex_lock(&q->mutex);
    uint32_t group = q->negotiated_group;
    pthread_mutex_unlock(&q->mutex);
    return group;
}

QP_EXPORT void qp_close(qp_context* q) {
    if (!q) return;
    pthread_mutex_lock(&q->mutex);
    q->stopped = 1;
    pthread_cond_broadcast(&q->condition);
    pthread_mutex_unlock(&q->mutex);
    if (q->listener) { q->api->ListenerStop(q->listener); q->api->ListenerClose(q->listener); }
    qp_stream* pending = q->streams_head;
    q->streams_head = q->streams_tail = NULL;
    while (pending) {
        qp_stream* next = pending->next;
        qp_stream_close(pending);
        pending = next;
    }
    pthread_mutex_lock(&q->mutex);
    HQUIC connection = q->connection;
    pthread_mutex_unlock(&q->mutex);
    if (connection) {
        q->api->ConnectionShutdown(connection, QUIC_CONNECTION_SHUTDOWN_FLAG_NONE, 0);
        q->api->ConnectionClose(connection);
    }
    // Server handshakes that never became the session close themselves on
    // SHUTDOWN_COMPLETE; RegistrationClose waits for them, so q must stay valid.
    if (q->registration) q->api->RegistrationShutdown(q->registration, QUIC_CONNECTION_SHUTDOWN_FLAG_SILENT, 0);
    if (q->configuration) q->api->ConfigurationClose(q->configuration);
    if (q->registration) q->api->RegistrationClose(q->registration);
    if (q->api) MsQuicClose(q->api);
    while (q->datagrams.head) qp_datagram_drop(&q->datagrams);
    free(q->peer_cert);
    pthread_cond_destroy(&q->condition);
    pthread_mutex_destroy(&q->mutex);
    free(q);
}
