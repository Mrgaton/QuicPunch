#include "qp_datagram_queue.h"
#include <assert.h>
#include <stdio.h>
int main(void) {
    qp_datagram_queue q = {0};
    unsigned char data[65535] = {42}, out[65535];
    for (unsigned i = 0; i < 10000; ++i) {
        assert(qp_datagram_enqueue(&q, data, sizeof(data), 100));
        assert(q.bytes <= QP_DATAGRAM_MAX_BYTES && q.count <= QP_DATAGRAM_MAX_COUNT);
    }
    assert(q.dropped > 0);
    qp_datagram_expire(&q, 350);
    assert(q.count == 0 && q.bytes == 0);
    for (unsigned i = 0; i < 300; ++i) { data[0] = (unsigned char)i; qp_datagram_enqueue(&q, data, 1, 400); }
    assert(q.count == QP_DATAGRAM_MAX_COUNT);
    assert(qp_datagram_dequeue(&q, out, sizeof(out), 401) == 1 && out[0] == 44);
    while (q.head) qp_datagram_drop(&q);
    assert(!q.bytes && !q.count && !q.tail);
    assert(!qp_datagram_enqueue(&q, data, 65536, 500));
    puts("PASS bounded Android datagram queue: bytes, count, age, newest packets, cleanup");
}
