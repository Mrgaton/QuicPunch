#ifndef QP_DATAGRAM_QUEUE_H
#define QP_DATAGRAM_QUEUE_H
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#define QP_DATAGRAM_MAX_BYTES (256u * 1024u)
#define QP_DATAGRAM_MAX_COUNT 256u
#define QP_DATAGRAM_MAX_AGE_MS 250u

typedef struct qp_datagram {
    struct qp_datagram* next;
    size_t size;
    uint64_t received_ms;
    unsigned char data[];
} qp_datagram;
typedef struct qp_datagram_queue {
    qp_datagram *head, *tail;
    size_t bytes, count;
    uint64_t dropped;
} qp_datagram_queue;

// The caller holds the connection mutex. Limits apply before allocating.
static void qp_datagram_drop(qp_datagram_queue* q) {
    qp_datagram* p = q->head;
    if (!p) return;
    q->head = p->next;
    if (!q->head) q->tail = NULL;
    q->bytes -= p->size; --q->count;
    free(p);
}
static void qp_datagram_expire(qp_datagram_queue* q, uint64_t now) {
    while (q->head && now - q->head->received_ms >= QP_DATAGRAM_MAX_AGE_MS) {
        qp_datagram_drop(q); ++q->dropped;
    }
}
static int qp_datagram_enqueue(qp_datagram_queue* q, const unsigned char* data, size_t size, uint64_t now) {
    if (!size || size > 65535 || size > QP_DATAGRAM_MAX_BYTES) return 0;
    qp_datagram_expire(q, now);
    while (q->head && (q->count >= QP_DATAGRAM_MAX_COUNT || q->bytes + size > QP_DATAGRAM_MAX_BYTES)) {
        qp_datagram_drop(q); ++q->dropped;
    }
    qp_datagram* p = malloc(sizeof(*p) + size);
    if (!p) { ++q->dropped; return 0; }
    p->next = NULL; p->size = size; p->received_ms = now;
    memcpy(p->data, data, size);
    if (q->tail) q->tail->next = p; else q->head = p;
    q->tail = p; q->bytes += size; ++q->count;
    return 1;
}
static int qp_datagram_dequeue(qp_datagram_queue* q, unsigned char* data, size_t capacity, uint64_t now) {
    qp_datagram_expire(q, now);
    if (!q->head) return -3;
    int size = q->head->size <= capacity ? (int)q->head->size : -4;
    if (size > 0) memcpy(data, q->head->data, (size_t)size);
    else ++q->dropped;
    qp_datagram_drop(q);
    return size;
}
#endif
