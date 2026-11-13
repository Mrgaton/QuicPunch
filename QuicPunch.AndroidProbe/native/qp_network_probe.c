// Standalone authenticated transport probe: two independent hosts/processes.
// No discovery server, relay or TURN. Test certificates must be provisioned out of band.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#include <windows.h>
#define pause_ms(ms) Sleep(ms)
#else
#include <unistd.h>
#define pause_ms(ms) usleep((ms)*1000)
#endif
typedef struct qp_context qp_context;
typedef struct qp_stream qp_stream;
extern qp_context* qp_create(const unsigned char*, unsigned, const unsigned char*, unsigned, int);
extern int qp_public_key_hash(const unsigned char*, unsigned, unsigned char*);
extern int qp_listen(qp_context*, unsigned short);
extern int qp_connect(qp_context*, const char*, unsigned short, unsigned short, const char*);
extern int qp_wait_connected(qp_context*, int);
extern qp_stream* qp_open_stream(qp_context*, int);
extern qp_stream* qp_accept_stream(qp_context*, int);
extern int qp_stream_write(qp_stream*, const unsigned char*, int);
extern int qp_stream_read(qp_stream*, unsigned char*, int, int);
extern void qp_stream_close(qp_stream*);
extern void qp_stream_complete(qp_stream*);
extern int qp_stream_writes_closed(qp_stream*);
extern int qp_send_datagram(qp_context*, const unsigned char*, int);
extern int qp_receive_datagram(qp_context*, unsigned char*, int, int);
extern int qp_datagram_state(qp_context*);
extern uint32_t qp_get_tls_group(qp_context*);
extern int qp_get_address(qp_context*, int, char*, int, unsigned short*);
extern int qp_get_stats(qp_context*, uint64_t*, int);
extern void qp_close(qp_context*);
static unsigned char* load(const char* path, unsigned* length) {
    FILE* f = fopen(path,"rb"); if (!f) return NULL;
    fseek(f,0,SEEK_END); long n=ftell(f); rewind(f);
    if (n<=0 || n>65536) { fclose(f); return NULL; }
    unsigned char* data=malloc((size_t)n);
    if (!data || fread(data,1,(size_t)n,f)!=(size_t)n) { fclose(f); free(data); return NULL; }
    fclose(f); *length=(unsigned)n; return data;
}
int main(int argc, char** argv) {
    if (argc != 7) { fprintf(stderr,"usage: qp_network_probe server|client own.pfx peer.der host port local-port\n"); return 2; }
    setvbuf(stdout,NULL,_IONBF,0);
    int server = strcmp(argv[1],"server")==0;
    unsigned pn=0,dn=0; unsigned char* pfx=load(argv[2],&pn), *der=load(argv[3],&dn), hash[32];
    if (!pfx || !der || !qp_public_key_hash(der,dn,hash)) return 3;
    qp_context* q=qp_create(pfx,pn,hash,32,server);
    memset(pfx,0,pn); free(pfx); free(der);
    if (!q) return 4;
    int status=server ? qp_listen(q,(unsigned short)atoi(argv[5])) :
        qp_connect(q,argv[4],(unsigned short)atoi(argv[5]),(unsigned short)atoi(argv[6]),"cloudflare-quic.com");
    if (status) { fprintf(stderr,"start status=0x%x\n",status); qp_close(q); return 5; }
    printf("READY %s\n",argv[1]);
    status=-3;
    for(int i=0;i<600 && status==-3;i++) status=qp_wait_connected(q,100);
    if (status) { fprintf(stderr,"handshake status=0x%x\n",status); qp_close(q); return 6; }
    char address[64]; unsigned short port=0;
    qp_get_address(q,0,address,sizeof(address),&port); printf("LOCAL %s:%u\n",address,port);
    qp_get_address(q,1,address,sizeof(address),&port); printf("REMOTE %s:%u\n",address,port);
    uint64_t stats[21]={0};
    int got_stats=qp_get_stats(q,stats,21);
    printf("TLS group=%u stats=%d cc=%llu\n",qp_get_tls_group(q),got_stats,(unsigned long long)stats[20]);
    if (qp_get_tls_group(q)!=4588 || !got_stats || stats[20]!=1) return 7;
    qp_stream* s=server ? NULL : qp_open_stream(q,1);
    unsigned char payload[1000],data[1000];
    for(int i=0;i<1000;i++) payload[i]=(unsigned char)(i*17+3);
    if (!server && (!s || qp_stream_write(s,payload,1000)!=1000)) return 8;
    for(int i=0;server && !s && i<100;i++) s=qp_accept_stream(q,100);
    if(!s) return 9;
    int received=0;
    for(int i=0;i<100 && received<1000;i++) { int n=qp_stream_read(s,data+received,1000-received,100); if(n>0) received+=n; else if(n!=-3) break; }
    if(received!=1000 || memcmp(data,payload,1000)) return 10;
    if(server && qp_stream_write(s,payload,1000)!=1000) return 11;
    qp_stream_complete(s);
    int fin=0;
    for(int i=0;i<100 && !fin;i++) { fin=qp_stream_writes_closed(s); pause_ms(10); }
    if(fin!=1) { fprintf(stderr,"FIN status=%d\n",fin); return 12; }
    int max=qp_datagram_state(q), successes=0;
    printf("DATAGRAM negotiated max=%d\n",max);
    if(max<1000) return 13;
    // Twenty individually identified datagrams; retries are test probes, never
    // transport retransmission. A reply must match the payload exactly.
    for(int i=0;i<20;i++) {
        payload[0]=(unsigned char)i;
        if(!server && qp_send_datagram(q,payload,1000)!=1000) return 14;
        int n=qp_receive_datagram(q,data,1000,300);
        if(n==1000) {
            if(server) { if(qp_send_datagram(q,data,1000)!=1000) return 15; successes++; }
            else if(!memcmp(data,payload,1000)) successes++;
        }
    }
    qp_get_stats(q,stats,21);
    printf("RESULT group=4588 cc=BBR stream=1000 FIN=ack datagrams=%d/20 max=%d mtu=%llu rtt_us=%llu\n",successes,max,(unsigned long long)stats[4],(unsigned long long)stats[0]);
    pause_ms(500); qp_stream_close(s); qp_close(q);
    return successes>=15 ? 0 : 16;
}
