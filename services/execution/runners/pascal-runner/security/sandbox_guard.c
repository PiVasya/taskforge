#define _GNU_SOURCE

#include <errno.h>
#include <linux/audit.h>
#include <linux/filter.h>
#include <linux/sched.h>
#include <linux/seccomp.h>
#include <stddef.h>
#include <stdlib.h>
#include <stdint.h>
#include <sys/prctl.h>
#include <sys/resource.h>
#include <sys/socket.h>
#include <sys/syscall.h>
#include <unistd.h>

#define TF_DENY_ACTION (SECCOMP_RET_ERRNO | (EPERM & SECCOMP_RET_DATA))
#define TF_ENOSYS_ACTION (SECCOMP_RET_ERRNO | (ENOSYS & SECCOMP_RET_DATA))
#define TF_DENY_SYSCALL(number) \
    BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, (number), 0, 1), \
    BPF_STMT(BPF_RET | BPF_K, TF_DENY_ACTION)
#define TF_ENOSYS_SYSCALL(number) \
    BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, (number), 0, 1), \
    BPF_STMT(BPF_RET | BPF_K, TF_ENOSYS_ACTION)
#define TF_ALLOW_SELF_PID_SYSCALL(number, argument_offset, allowed_pid) \
    BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, (number), 0, 4), \
    BPF_STMT(BPF_LD | BPF_W | BPF_ABS, (argument_offset)), \
    BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, (uint32_t)(allowed_pid), 1, 0), \
    BPF_STMT(BPF_RET | BPF_K, TF_DENY_ACTION), \
    BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, nr))

static long tf_raw_syscall6(long number, long arg1, long arg2, long arg3,
                            long arg4, long arg5, long arg6)
{
#if defined(__x86_64__)
    register long r10 __asm__("r10") = arg4;
    register long r8 __asm__("r8") = arg5;
    register long r9 __asm__("r9") = arg6;
    long result;
    __asm__ volatile(
        "syscall"
        : "=a"(result)
        : "a"(number), "D"(arg1), "S"(arg2), "d"(arg3),
          "r"(r10), "r"(r8), "r"(r9)
        : "rcx", "r11", "cc", "memory");
    return result;
#elif defined(__aarch64__)
    register long x0 __asm__("x0") = arg1;
    register long x1 __asm__("x1") = arg2;
    register long x2 __asm__("x2") = arg3;
    register long x3 __asm__("x3") = arg4;
    register long x4 __asm__("x4") = arg5;
    register long x5 __asm__("x5") = arg6;
    register long x8 __asm__("x8") = number;
    __asm__ volatile(
        "svc 0"
        : "+r"(x0)
        : "r"(x1), "r"(x2), "r"(x3), "r"(x4), "r"(x5), "r"(x8)
        : "cc", "memory");
    return x0;
#else
#error Unsupported architecture for TaskForge Pascal sandbox
#endif
}

__attribute__((noreturn))
static void tf_fail(void)
{
    static const char message[] = "TaskForge sandbox initialization failed.\n";
#ifdef __NR_write
    (void)tf_raw_syscall6(__NR_write, STDERR_FILENO,
                          (long)(uintptr_t)message,
                          (long)(sizeof(message) - 1), 0, 0, 0);
#endif
#ifdef __NR_exit_group
    (void)tf_raw_syscall6(__NR_exit_group, 126, 0, 0, 0, 0, 0);
#else
    (void)tf_raw_syscall6(__NR_exit, 126, 0, 0, 0, 0, 0);
#endif
    for (;;) { }
}

static void tf_set_limit(int resource, rlim_t value)
{
#ifdef __NR_prlimit64
    struct rlimit limit = { .rlim_cur = value, .rlim_max = value };
    if (tf_raw_syscall6(__NR_prlimit64, 0, resource,
                        (long)(uintptr_t)&limit, 0, 0, 0) != 0) {
        tf_fail();
    }
#else
#error prlimit64 is required for the TaskForge sandbox
#endif
}

static rlim_t tf_read_cpu_limit(void)
{
    const char *raw = getenv("TASKFORGE_LIMIT_CPU_SECONDS");
    if (raw == NULL || *raw == '\0') {
        return 40;
    }

    char *end = NULL;
    errno = 0;
    long parsed = strtol(raw, &end, 10);
    if (errno != 0 || end == raw || *end != '\0' || parsed < 1) {
        return 40;
    }
    if (parsed > 122) {
        parsed = 122;
    }
    return (rlim_t)parsed;
}

static void tf_apply_limits(void)
{
    tf_set_limit(RLIMIT_CORE, 0);
    tf_set_limit(RLIMIT_CPU, tf_read_cpu_limit());
    tf_set_limit(RLIMIT_FSIZE, (rlim_t)16 * 1024U * 1024U);
    tf_set_limit(RLIMIT_NOFILE, 128);
}

__attribute__((visibility("hidden")))
void taskforge_pascal_sandbox_init(void)
{
    tf_apply_limits();
    const uint32_t self_pid = (uint32_t)tf_raw_syscall6(__NR_getpid, 0, 0, 0, 0, 0, 0);
    struct sock_filter filter[] = {
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, arch)),
#if defined(__x86_64__)
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, AUDIT_ARCH_X86_64, 1, 0),
#elif defined(__aarch64__)
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, AUDIT_ARCH_AARCH64, 1, 0),
#endif
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_KILL_PROCESS),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, nr)),
#if defined(__x86_64__)
        BPF_JUMP(BPF_JMP | BPF_JGE | BPF_K, 0x40000000U, 0, 1),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_KILL_PROCESS),
#endif
#ifdef __NR_execve
        TF_DENY_SYSCALL(__NR_execve),
#endif
#ifdef __NR_execveat
        TF_DENY_SYSCALL(__NR_execveat),
#endif
#ifdef __NR_fork
        TF_DENY_SYSCALL(__NR_fork),
#endif
#ifdef __NR_vfork
        TF_DENY_SYSCALL(__NR_vfork),
#endif
#ifdef __NR_clone3
        TF_ENOSYS_SYSCALL(__NR_clone3),
#endif
#ifdef __NR_clone
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, __NR_clone, 0, 5),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, args[0])),
        BPF_STMT(BPF_ALU | BPF_AND | BPF_K, CLONE_VM | CLONE_THREAD),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, CLONE_VM | CLONE_THREAD, 1, 0),
        BPF_STMT(BPF_RET | BPF_K, TF_DENY_ACTION),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, nr)),
#endif
#ifdef __NR_kill
        TF_ALLOW_SELF_PID_SYSCALL(__NR_kill, offsetof(struct seccomp_data, args[0]), self_pid),
#endif
#ifdef __NR_tgkill
        TF_ALLOW_SELF_PID_SYSCALL(__NR_tgkill, offsetof(struct seccomp_data, args[0]), self_pid),
#endif
#ifdef __NR_tkill
        TF_DENY_SYSCALL(__NR_tkill),
#endif
#ifdef __NR_ptrace
        TF_DENY_SYSCALL(__NR_ptrace),
#endif
#ifdef __NR_process_vm_readv
        TF_DENY_SYSCALL(__NR_process_vm_readv),
#endif
#ifdef __NR_process_vm_writev
        TF_DENY_SYSCALL(__NR_process_vm_writev),
#endif
#ifdef __NR_process_madvise
        TF_DENY_SYSCALL(__NR_process_madvise),
#endif
#ifdef __NR_process_mrelease
        TF_DENY_SYSCALL(__NR_process_mrelease),
#endif
#ifdef __NR_pidfd_open
        TF_DENY_SYSCALL(__NR_pidfd_open),
#endif
#ifdef __NR_pidfd_getfd
        TF_DENY_SYSCALL(__NR_pidfd_getfd),
#endif
#ifdef __NR_pidfd_send_signal
        TF_DENY_SYSCALL(__NR_pidfd_send_signal),
#endif
#ifdef __NR_kcmp
        TF_DENY_SYSCALL(__NR_kcmp),
#endif
#ifdef __NR_socket
        TF_DENY_SYSCALL(__NR_socket),
#endif
#ifdef __NR_socketpair
        TF_DENY_SYSCALL(__NR_socketpair),
#endif
#ifdef __NR_connect
        TF_DENY_SYSCALL(__NR_connect),
#endif
#ifdef __NR_bind
        TF_DENY_SYSCALL(__NR_bind),
#endif
#ifdef __NR_listen
        TF_DENY_SYSCALL(__NR_listen),
#endif
#ifdef __NR_accept
        TF_DENY_SYSCALL(__NR_accept),
#endif
#ifdef __NR_accept4
        TF_DENY_SYSCALL(__NR_accept4),
#endif
#ifdef __NR_sendto
        TF_DENY_SYSCALL(__NR_sendto),
#endif
#ifdef __NR_sendmsg
        TF_DENY_SYSCALL(__NR_sendmsg),
#endif
#ifdef __NR_sendmmsg
        TF_DENY_SYSCALL(__NR_sendmmsg),
#endif
#ifdef __NR_recvfrom
        TF_DENY_SYSCALL(__NR_recvfrom),
#endif
#ifdef __NR_recvmsg
        TF_DENY_SYSCALL(__NR_recvmsg),
#endif
#ifdef __NR_recvmmsg
        TF_DENY_SYSCALL(__NR_recvmmsg),
#endif
#ifdef __NR_shutdown
        TF_DENY_SYSCALL(__NR_shutdown),
#endif
#ifdef __NR_mount
        TF_DENY_SYSCALL(__NR_mount),
#endif
#ifdef __NR_umount2
        TF_DENY_SYSCALL(__NR_umount2),
#endif
#ifdef __NR_pivot_root
        TF_DENY_SYSCALL(__NR_pivot_root),
#endif
#ifdef __NR_chroot
        TF_DENY_SYSCALL(__NR_chroot),
#endif
#ifdef __NR_unshare
        TF_DENY_SYSCALL(__NR_unshare),
#endif
#ifdef __NR_setns
        TF_DENY_SYSCALL(__NR_setns),
#endif
#ifdef __NR_sethostname
        TF_DENY_SYSCALL(__NR_sethostname),
#endif
#ifdef __NR_setdomainname
        TF_DENY_SYSCALL(__NR_setdomainname),
#endif
#ifdef __NR_bpf
        TF_DENY_SYSCALL(__NR_bpf),
#endif
#ifdef __NR_perf_event_open
        TF_DENY_SYSCALL(__NR_perf_event_open),
#endif
#ifdef __NR_userfaultfd
        TF_DENY_SYSCALL(__NR_userfaultfd),
#endif
#ifdef __NR_io_uring_setup
        TF_DENY_SYSCALL(__NR_io_uring_setup),
#endif
#ifdef __NR_io_uring_enter
        TF_DENY_SYSCALL(__NR_io_uring_enter),
#endif
#ifdef __NR_io_uring_register
        TF_DENY_SYSCALL(__NR_io_uring_register),
#endif
#ifdef __NR_keyctl
        TF_DENY_SYSCALL(__NR_keyctl),
#endif
#ifdef __NR_add_key
        TF_DENY_SYSCALL(__NR_add_key),
#endif
#ifdef __NR_request_key
        TF_DENY_SYSCALL(__NR_request_key),
#endif
#ifdef __NR_kexec_load
        TF_DENY_SYSCALL(__NR_kexec_load),
#endif
#ifdef __NR_kexec_file_load
        TF_DENY_SYSCALL(__NR_kexec_file_load),
#endif
#ifdef __NR_init_module
        TF_DENY_SYSCALL(__NR_init_module),
#endif
#ifdef __NR_finit_module
        TF_DENY_SYSCALL(__NR_finit_module),
#endif
#ifdef __NR_delete_module
        TF_DENY_SYSCALL(__NR_delete_module),
#endif
#ifdef __NR_reboot
        TF_DENY_SYSCALL(__NR_reboot),
#endif
#ifdef __NR_swapon
        TF_DENY_SYSCALL(__NR_swapon),
#endif
#ifdef __NR_swapoff
        TF_DENY_SYSCALL(__NR_swapoff),
#endif
#ifdef __NR_open_by_handle_at
        TF_DENY_SYSCALL(__NR_open_by_handle_at),
#endif
#ifdef __NR_name_to_handle_at
        TF_DENY_SYSCALL(__NR_name_to_handle_at),
#endif
#ifdef __NR_quotactl
        TF_DENY_SYSCALL(__NR_quotactl),
#endif
#ifdef __NR_quotactl_fd
        TF_DENY_SYSCALL(__NR_quotactl_fd),
#endif
#ifdef __NR_fanotify_init
        TF_DENY_SYSCALL(__NR_fanotify_init),
#endif
#ifdef __NR_iopl
        TF_DENY_SYSCALL(__NR_iopl),
#endif
#ifdef __NR_ioperm
        TF_DENY_SYSCALL(__NR_ioperm),
#endif
#ifdef __NR_memfd_create
        TF_DENY_SYSCALL(__NR_memfd_create),
#endif
#ifdef __NR_mmap
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, __NR_mmap, 0, 3),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, args[2])),
        BPF_JUMP(BPF_JMP | BPF_JSET | BPF_K, 0x4U, 0, 1),
        BPF_STMT(BPF_RET | BPF_K, TF_DENY_ACTION),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, nr)),
#endif
#ifdef __NR_mprotect
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, __NR_mprotect, 0, 3),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, args[2])),
        BPF_JUMP(BPF_JMP | BPF_JSET | BPF_K, 0x4U, 0, 1),
        BPF_STMT(BPF_RET | BPF_K, TF_DENY_ACTION),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, nr)),
#endif
#ifdef __NR_pkey_mprotect
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, __NR_pkey_mprotect, 0, 3),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, args[2])),
        BPF_JUMP(BPF_JMP | BPF_JSET | BPF_K, 0x4U, 0, 1),
        BPF_STMT(BPF_RET | BPF_K, TF_DENY_ACTION),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, nr)),
#endif
#ifdef __NR_prctl
        TF_DENY_SYSCALL(__NR_prctl),
#endif
#ifdef __NR_seccomp
        TF_DENY_SYSCALL(__NR_seccomp),
#endif
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ALLOW),
    };
    struct sock_fprog program = {
        .len = (unsigned short)(sizeof(filter) / sizeof(filter[0])),
        .filter = filter,
    };

    if (tf_raw_syscall6(__NR_prctl, PR_SET_DUMPABLE, 0, 0, 0, 0, 0) != 0) {
        tf_fail();
    }
    if (tf_raw_syscall6(__NR_prctl, PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0, 0) != 0) {
        tf_fail();
    }
    if (tf_raw_syscall6(__NR_prctl, PR_SET_SECCOMP, SECCOMP_MODE_FILTER,
                        (long)(uintptr_t)&program, 0, 0, 0) != 0) {
        tf_fail();
    }
}
