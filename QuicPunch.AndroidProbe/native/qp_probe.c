#include <msquic.h>
#include <openssl/evp.h>
#include <openssl/x509.h>
#include <pthread.h>
#include <stdio.h>
#include <string.h>
#include <time.h>

typedef struct {
    const QUIC_API_TABLE* api;
    pthread_mutex_t mutex;
    pthread_cond_t condition;
    int done;
    int connected;
    int got_pong;
    char message[160];
    HQUIC stream;
    QUIC_BUFFER send_buffer;
    char send_bytes[4];
    char receive_bytes[4];
    unsigned receive_length;
    unsigned char expected_fingerprint[32];
    int certificate_seen;
} PROBE;

static int hex_digit(char value) {
    if (value >= '0' && value <= '9') return value - '0';
    if (value >= 'a' && value <= 'f') return value - 'a' + 10;
    if (value >= 'A' && value <= 'F') return value - 'A' + 10;
    return -1;
}

static int parse_fingerprint(const char* input, unsigned char* output) {
    if (!input || strlen(input) != 64) return 0;
    for (int i = 0; i < 32; ++i) {
        int high = hex_digit(input[i * 2]);
        int low = hex_digit(input[i * 2 + 1]);
        if (high < 0 || low < 0) return 0;
        output[i] = (unsigned char)((high << 4) | low);
    }
    return 1;
}

static void finish(PROBE* probe, const char* message, int pong) {
    pthread_mutex_lock(&probe->mutex);
    if (!probe->done) {
        probe->got_pong = pong;
        snprintf(probe->message, sizeof(probe->message), "%s", message);
        probe->done = 1;
        pthread_cond_signal(&probe->condition);
    }
    pthread_mutex_unlock(&probe->mutex);
}

static QUIC_STATUS QUIC_API stream_callback(HQUIC stream, void* context, QUIC_STREAM_EVENT* event) {
    PROBE* probe = (PROBE*)context;
    if (event->Type == QUIC_STREAM_EVENT_RECEIVE) {
        for (uint32_t i = 0; i < event->RECEIVE.BufferCount; ++i) {
            const QUIC_BUFFER* buffer = &event->RECEIVE.Buffers[i];
            for (uint32_t j = 0; j < buffer->Length && probe->receive_length < 4; ++j)
                probe->receive_bytes[probe->receive_length++] = (char)buffer->Buffer[j];
        }
        if (probe->receive_length == 4)
            finish(probe, memcmp(probe->receive_bytes, "PONG", 4) == 0 ? "PONG received" : "Unexpected reply", memcmp(probe->receive_bytes, "PONG", 4) == 0);
    } else if (event->Type == QUIC_STREAM_EVENT_PEER_SEND_ABORTED) {
        finish(probe, "Peer aborted the stream", 0);
    }
    (void)stream;
    return QUIC_STATUS_SUCCESS;
}

static QUIC_STATUS QUIC_API connection_callback(HQUIC connection, void* context, QUIC_CONNECTION_EVENT* event) {
    PROBE* probe = (PROBE*)context;
    if (event->Type == QUIC_CONNECTION_EVENT_CONNECTED) {
        if (!probe->certificate_seen) { finish(probe, "Server certificate was not checked", 0); return QUIC_STATUS_BAD_CERTIFICATE; }
        probe->connected = 1;
        QUIC_STATUS status = probe->api->StreamOpen(connection, QUIC_STREAM_OPEN_FLAG_NONE, stream_callback, probe, &probe->stream);
        if (QUIC_FAILED(status)) { finish(probe, "StreamOpen failed", 0); return QUIC_STATUS_SUCCESS; }
        status = probe->api->StreamStart(probe->stream, QUIC_STREAM_START_FLAG_NONE);
        if (QUIC_FAILED(status)) { finish(probe, "StreamStart failed", 0); return QUIC_STATUS_SUCCESS; }
        memcpy(probe->send_bytes, "PING", 4);
        probe->send_buffer.Buffer = (uint8_t*)probe->send_bytes;
        probe->send_buffer.Length = 4;
        status = probe->api->StreamSend(probe->stream, &probe->send_buffer, 1, QUIC_SEND_FLAG_FIN, NULL);
        if (QUIC_FAILED(status)) finish(probe, "StreamSend failed", 0);
    } else if (event->Type == QUIC_CONNECTION_EVENT_SHUTDOWN_INITIATED_BY_TRANSPORT) {
        char message[120];
        snprintf(message, sizeof(message), "Transport closed: 0x%x", event->SHUTDOWN_INITIATED_BY_TRANSPORT.Status);
        finish(probe, message, 0);
    } else if (event->Type == QUIC_CONNECTION_EVENT_SHUTDOWN_INITIATED_BY_PEER) {
        finish(probe, "Peer closed connection", 0);
    } else if (event->Type == QUIC_CONNECTION_EVENT_PEER_CERTIFICATE_RECEIVED) {
        unsigned char digest[EVP_MAX_MD_SIZE];
        unsigned digest_length = 0;
        X509* certificate = (X509*)event->PEER_CERTIFICATE_RECEIVED.Certificate;
        if (!certificate || X509_digest(certificate, EVP_sha256(), digest, &digest_length) != 1 ||
            digest_length != 32 || memcmp(digest, probe->expected_fingerprint, 32) != 0) {
            finish(probe, "Server certificate fingerprint mismatch", 0);
            return QUIC_STATUS_BAD_CERTIFICATE;
        }
        probe->certificate_seen = 1;
    }
    return QUIC_STATUS_SUCCESS;
}

// The system trust chain is bypassed for this self-signed test certificate;
// the exact SHA-256 fingerprint is checked in the certificate callback.
__attribute__((visibility("default")))
int qp_probe_ping(const char* host, unsigned short port, const char* fingerprint, char* output, unsigned output_capacity) {
    if (!host || !output || output_capacity == 0) return 0;
    const QUIC_API_TABLE* api = NULL;
    HQUIC registration = NULL, configuration = NULL, connection = NULL;
    PROBE probe = {0};
    pthread_mutex_init(&probe.mutex, NULL);
    pthread_cond_init(&probe.condition, NULL);
    int success = 0;
    if (!parse_fingerprint(fingerprint, probe.expected_fingerprint)) {
        snprintf(probe.message, sizeof(probe.message), "Expected a 64-digit SHA-256 certificate fingerprint");
        goto cleanup;
    }
    QUIC_STATUS status = MsQuicOpen2(&api);
    if (QUIC_FAILED(status)) { snprintf(probe.message, sizeof(probe.message), "MsQuicOpen2: 0x%x", status); goto cleanup; }
    probe.api = api;
    QUIC_REGISTRATION_CONFIG registration_config = { "qp-android-probe", QUIC_EXECUTION_PROFILE_LOW_LATENCY };
    status = api->RegistrationOpen(&registration_config, &registration);
    if (QUIC_FAILED(status)) { snprintf(probe.message, sizeof(probe.message), "RegistrationOpen: 0x%x", status); goto cleanup; }
    uint8_t alpn_bytes[] = "qp-android-probe-1";
    QUIC_BUFFER alpn = { (uint32_t)(sizeof(alpn_bytes) - 1), alpn_bytes };
    status = api->ConfigurationOpen(registration, &alpn, 1, NULL, 0, NULL, &configuration);
    if (QUIC_FAILED(status)) { snprintf(probe.message, sizeof(probe.message), "ConfigurationOpen: 0x%x", status); goto cleanup; }
    QUIC_CREDENTIAL_CONFIG credential = {0};
    credential.Type = QUIC_CREDENTIAL_TYPE_NONE;
    credential.Flags = QUIC_CREDENTIAL_FLAG_CLIENT | QUIC_CREDENTIAL_FLAG_NO_CERTIFICATE_VALIDATION |
        QUIC_CREDENTIAL_FLAG_INDICATE_CERTIFICATE_RECEIVED;
    status = api->ConfigurationLoadCredential(configuration, &credential);
    if (QUIC_FAILED(status)) { snprintf(probe.message, sizeof(probe.message), "LoadCredential: 0x%x", status); goto cleanup; }
    status = api->ConnectionOpen(registration, connection_callback, &probe, &connection);
    if (QUIC_FAILED(status)) { snprintf(probe.message, sizeof(probe.message), "ConnectionOpen: 0x%x", status); goto cleanup; }
    status = api->ConnectionStart(connection, configuration, QUIC_ADDRESS_FAMILY_UNSPEC, host, port);
    if (QUIC_FAILED(status)) { snprintf(probe.message, sizeof(probe.message), "ConnectionStart: 0x%x", status); goto cleanup; }
    struct timespec deadline;
    clock_gettime(CLOCK_REALTIME, &deadline);
    deadline.tv_sec += 15;
    pthread_mutex_lock(&probe.mutex);
    while (!probe.done) {
        if (pthread_cond_timedwait(&probe.condition, &probe.mutex, &deadline) != 0) {
            snprintf(probe.message, sizeof(probe.message), "Timed out waiting for PONG");
            break;
        }
    }
    success = probe.got_pong;
    pthread_mutex_unlock(&probe.mutex);
cleanup:
    if (connection) {
        api->ConnectionShutdown(connection, QUIC_CONNECTION_SHUTDOWN_FLAG_NONE, 0);
        api->ConnectionClose(connection);
    }
    if (probe.stream) api->StreamClose(probe.stream);
    if (configuration) api->ConfigurationClose(configuration);
    if (registration) api->RegistrationClose(registration);
    if (api) MsQuicClose(api);
    snprintf(output, output_capacity, "%s", probe.message[0] ? probe.message : "Unknown failure");
    pthread_cond_destroy(&probe.condition);
    pthread_mutex_destroy(&probe.mutex);
    return success;
}
