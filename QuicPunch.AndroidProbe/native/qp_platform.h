#pragma once
#include <time.h>
#include <errno.h>
#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#define QP_EXPORT __declspec(dllexport)
typedef CRITICAL_SECTION pthread_mutex_t;
typedef CONDITION_VARIABLE pthread_cond_t;
static int pthread_mutex_init(pthread_mutex_t* p, void* unused) { (void)unused; InitializeCriticalSection(p); return 0; }
static void pthread_mutex_destroy(pthread_mutex_t* p) { DeleteCriticalSection(p); }
static void pthread_mutex_lock(pthread_mutex_t* p) { EnterCriticalSection(p); }
static void pthread_mutex_unlock(pthread_mutex_t* p) { LeaveCriticalSection(p); }
static int pthread_cond_init(pthread_cond_t* p, void* unused) { (void)unused; InitializeConditionVariable(p); return 0; }
static void pthread_cond_destroy(pthread_cond_t* p) { (void)p; }
static void pthread_cond_broadcast(pthread_cond_t* p) { WakeAllConditionVariable(p); }
static int pthread_cond_timedwait(pthread_cond_t* c, pthread_mutex_t* m, const struct timespec* end) {
    struct timespec now; timespec_get(&now, TIME_UTC);
    int64_t ms = (int64_t)(end->tv_sec - now.tv_sec) * 1000 + (end->tv_nsec - now.tv_nsec + 999999) / 1000000;
    DWORD timeout = ms <= 0 ? 0 : ms >= MAXDWORD ? MAXDWORD - 1 : (DWORD)ms;
    return SleepConditionVariableCS(c, m, timeout) ? 0 : ETIMEDOUT;
}
static uint64_t monotonic_ms(void) { return GetTickCount64(); }
#else
#include <pthread.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#define QP_EXPORT __attribute__((visibility("default")))
static uint64_t monotonic_ms(void) {
    struct timespec now; clock_gettime(CLOCK_MONOTONIC, &now);
    return (uint64_t)now.tv_sec * 1000 + (uint64_t)now.tv_nsec / 1000000;
}
#endif
