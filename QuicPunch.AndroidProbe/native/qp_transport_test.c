#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <pthread.h>
#include <unistd.h>

typedef struct qp_context qp_context;
typedef struct qp_stream qp_stream;
extern qp_context* qp_create(const unsigned char*, unsigned, const unsigned char*, unsigned, int);
extern int qp_listen(qp_context*, unsigned short);
extern int qp_connect(qp_context*, const char*, unsigned short, unsigned short, const char*);
extern int qp_public_key_hash(const unsigned char*, unsigned, unsigned char*);
extern int qp_get_peer_certificate(qp_context*, unsigned char*, int);
extern int qp_is_stopped(qp_context*);
extern int qp_wait_connected(qp_context*, int);
extern qp_stream* qp_open_stream(qp_context*, int);
extern qp_stream* qp_accept_stream(qp_context*, int);
extern int qp_stream_write(qp_stream*, const unsigned char*, int);
extern int qp_stream_read(qp_stream*, unsigned char*, int, int);
extern uint64_t qp_stream_id(qp_stream*);
extern int qp_stream_set_priority(qp_stream*, unsigned short);
extern unsigned short qp_stream_get_priority(qp_stream*);
extern int qp_stream_flush(qp_stream*, int);
extern void qp_stream_complete(qp_stream*);
extern int qp_stream_writes_closed(qp_stream*);
extern void qp_stream_abort(qp_stream*, uint64_t);
extern void qp_stream_close(qp_stream*);
extern void qp_close(qp_context*);
extern int qp_send_datagram(qp_context*, const unsigned char*, int);
extern int qp_receive_datagram(qp_context*, unsigned char*, int, int);
extern int qp_datagram_state(qp_context*);
extern int qp_get_address(qp_context*, int, char*, int, unsigned short*);
extern int qp_get_tls(qp_context*, char*, int, unsigned short*);
extern uint32_t qp_get_tls_group(qp_context*);
extern int qp_get_stats(qp_context*, uint64_t*, int);

static unsigned char* load(const char* path, unsigned* length) {
    FILE* file = fopen(path, "rb");
    if (!file) return NULL;
    fseek(file, 0, SEEK_END);
    long size = ftell(file);
    rewind(file);
    unsigned char* data = malloc(size);
    if (!data || fread(data, 1, size, file) != (size_t)size) { fclose(file); free(data); return NULL; }
    fclose(file);
    *length = size;
    return data;
}

// Usage: qp_transport_test <own.pfx> <expected-peer.der> [intruder.pfx]
// With an intruder identity, a rejected handshake is made first and the real
// session must still be established on the same listener.
int main(int argc, char** argv) {
    if (argc != 3 && argc != 4) return 2;
    const char* loopback = getenv("QP_TEST_IPV6") ? "::1" : "127.0.0.1";
    unsigned pfx_length = 0, der_length = 0;
    unsigned char* pfx = load(argv[1], &pfx_length);
    unsigned char* der = load(argv[2], &der_length);
    if (!pfx || !der) return 3;
    unsigned char hash[32];
    if (!qp_public_key_hash(der, der_length, hash)) return 3;
    qp_context* server = qp_create(pfx, pfx_length, hash, sizeof(hash), 1);
    qp_context* client = qp_create(pfx, pfx_length, hash, sizeof(hash), 0);
    if (!server || !client) { fprintf(stderr, "create failed\n"); return 4; }
    int status = qp_listen(server, 44777);
    fprintf(stderr, "listen: 0x%x\n", status);
    if (status != 0) return 5;
    if (argc == 4) {
        unsigned intruder_length = 0;
        unsigned char* intruder_pfx = load(argv[3], &intruder_length);
        qp_context* intruder = intruder_pfx ? qp_create(intruder_pfx, intruder_length, hash, sizeof(hash), 0) : NULL;
        if (!intruder || qp_connect(intruder, loopback, 44777, 44889, "cloudflare-quic.com") != 0) return 25;
        // In TLS 1.3 the client finishes before the server checks its certificate,
        // so the intruder may see CONNECTED; the server must still reject and close it.
        for (int i = 0; i < 100 && !qp_is_stopped(intruder); ++i) usleep(100 * 1000);
        fprintf(stderr, "intruder closed by server: %d\n", qp_is_stopped(intruder));
        if (!qp_is_stopped(intruder) || qp_wait_connected(server, 100) == 0) return 26;
        qp_close(intruder);
        free(intruder_pfx);
        if (qp_is_stopped(server)) return 27;
        puts("PASS intruder rejected without blocking the listener");
    }
    status = qp_connect(client, loopback, 44777, 44888, "cloudflare-quic.com");
    fprintf(stderr, "connect start: 0x%x\n", status);
    if (status != 0) return 6;
    int server_status = -3, client_status = -3;
    // A rejected server certificate fails the client while the server keeps listening.
    for (int i = 0; i < 100 && (client_status == -3 || (client_status == 0 && server_status == -3)); ++i) {
        if (server_status == -3) server_status = qp_wait_connected(server, 100);
        if (client_status == -3) client_status = qp_wait_connected(client, 100);
    }
    fprintf(stderr, "TLS: server 0x%x, client 0x%x\n", server_status, client_status);
    if (server_status != 0 || client_status != 0) return 7;
    char address[64] = {0}; unsigned short endpoint_port = 0;
    if (!qp_get_address(client, 1, address, sizeof(address), &endpoint_port) ||
        strcmp(address, loopback) != 0 || endpoint_port != 44777) return 14;
    char alpn[32] = {0}; unsigned short cipher = 0;
    if (!qp_get_tls(client, alpn, sizeof(alpn), &cipher) || strcmp(alpn, "h3") != 0 || cipher == 0) return 20;
    uint32_t client_group = qp_get_tls_group(client), server_group = qp_get_tls_group(server);
    fprintf(stderr, "TLS groups: client %u, server %u\n", client_group, server_group);
    if (client_group != 4588 || server_group != 4588) return 24;
    unsigned char peer_der[8192];
    int peer_length = qp_get_peer_certificate(server, peer_der, sizeof(peer_der));
    if (peer_length != (int)der_length || memcmp(peer_der, der, der_length) != 0) return 28;
    uint64_t stats[21] = {0};
    if (!qp_get_stats(client, stats, 21) || stats[4] < 1200 || stats[20] != 1) return 21;
    if (!qp_get_stats(server, stats, 21) || stats[20] != 1) return 29;
    fprintf(stderr, "BBR configured before handshake on client and server.\n");
    qp_stream* outbound = qp_open_stream(client, 1);
    if (!outbound) { fprintf(stderr, "open stream failed\n"); return 8; }
    if (!qp_stream_set_priority(outbound, 0xff00) || qp_stream_get_priority(outbound) != 0xff00) return 22;
    unsigned char payload[] = { 'Q', 'P' };
    status = qp_stream_write(outbound, payload, 2);
    fprintf(stderr, "send: %d\n", status);
    if (status != 2) return 9;
    status = -3;
    for (int i = 0; i < 100 && status == -3; ++i) status = qp_stream_flush(outbound, 100);
    if (status != 0) return 23;
    qp_stream* inbound = NULL;
    for (int i = 0; i < 100 && !inbound; ++i) inbound = qp_accept_stream(server, 100);
    if (!inbound) { fprintf(stderr, "accept stream timed out\n"); return 10; }
    unsigned char received[2];
    int count = -3;
    for (int i = 0; i < 100 && count == -3; ++i) count = qp_stream_read(inbound, received, 2, 100);
    fprintf(stderr, "receive: %d\n", count);
    if (count != 2 || memcmp(received, payload, 2) != 0) return 11;
    unsigned char response[] = { 'O', 'K' };
    if (qp_stream_write(inbound, response, 2) != 2) return 15;
    count = -3;
    for (int i = 0; i < 100 && count == -3; ++i)
        count = qp_stream_read(outbound, received, 2, 100);
    if (count != 2 || memcmp(received, response, 2) != 0) return 16;
    qp_stream_complete(outbound);
    int closed = 0;
    for (int i = 0; i < 100 && closed == 0; ++i) { closed = qp_stream_writes_closed(outbound); usleep(10000); }
    if (closed != 1) return 30;
    qp_stream* server_outbound = qp_open_stream(server, 1);
    if (!server_outbound || qp_stream_write(server_outbound, payload, 2) != 2) return 17;
    qp_stream* client_inbound = NULL;
    for (int i = 0; i < 100 && !client_inbound; ++i)
        client_inbound = qp_accept_stream(client, 100);
    if (!client_inbound || qp_stream_id(client_inbound) == qp_stream_id(outbound)) return 18;
    count = -3;
    for (int i = 0; i < 100 && count == -3; ++i)
        count = qp_stream_read(client_inbound, received, 2, 100);
    if (count != 2 || memcmp(received, payload, 2) != 0) return 19;
    int max_datagram = qp_datagram_state(client);
    fprintf(stderr, "datagrams: max %d\n", max_datagram);
    if (max_datagram < 2 || qp_send_datagram(client, payload, 2) != 2) return 12;
    count = -3;
    for (int i = 0; i < 100 && count == -3; ++i)
        count = qp_receive_datagram(server, received, 2, 100);
    if (count != 2 || memcmp(received, payload, 2) != 0) return 13;
    qp_stream_close(inbound);
    qp_stream_close(outbound);
    qp_stream_abort(server_outbound, 0x4D454449);
    qp_stream_close(server_outbound);
    qp_stream_close(client_inbound);
    qp_close(client);
    qp_close(server);
    free(pfx); free(der);
    puts("PASS native MsQuic loopback");
    return 0;
}
