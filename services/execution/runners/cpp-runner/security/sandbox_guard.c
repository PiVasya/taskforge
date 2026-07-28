#define _GNU_SOURCE

#include <errno.h>
#include <linux/audit.h>
#include <linux/filter.h>
#include <linux/seccomp.h>
#include <stddef.h>
#include <stdint.h>
#include <sys/mman.h>
#include <sys/prctl.h>
#include <sys/syscall.h>
#include <unistd.h>

#define TASKFORGE_DENY_ACTION (SECCOMP_RET_ERRNO | (EPERM & SECCOMP_RET_DATA))
#define TASKFORGE_DENY_SYSCALL(number) \
    BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, (number), 0, 1), \
    BPF_STMT(BPF_RET | BPF_K, TASKFORGE_DENY_ACTION)

static long taskforge_raw_syscall6(long number, long arg1, long arg2, long arg3,
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
#error Unsupported architecture for TaskForge C++ sandbox
#endif
}

__attribute__((noreturn))
static void taskforge_guard_fail(void)
{
    static const char message[] = "TaskForge sandbox initialization failed.\n";
#ifdef __NR_write
    (void)taskforge_raw_syscall6(__NR_write, STDERR_FILENO,
                                 (long)(uintptr_t)message,
                                 (long)(sizeof(message) - 1), 0, 0, 0);
#endif
#ifdef __NR_exit_group
    (void)taskforge_raw_syscall6(__NR_exit_group, 126, 0, 0, 0, 0, 0);
#elif defined(__NR_exit)
    (void)taskforge_raw_syscall6(__NR_exit, 126, 0, 0, 0, 0, 0);
#endif
    for (;;) {
    }
}

__attribute__((visibility("hidden")))
void taskforge_sandbox_init(void)
{
    struct sock_filter filter[] = {
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, arch)),
#if defined(__x86_64__)
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, AUDIT_ARCH_X86_64, 1, 0),
#elif defined(__aarch64__)
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, AUDIT_ARCH_AARCH64, 1, 0),
#else
#error Unsupported architecture for TaskForge C++ sandbox
#endif
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_KILL_PROCESS),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, nr)),

#ifdef __NR_execve
        TASKFORGE_DENY_SYSCALL(__NR_execve),
#endif
#ifdef __NR_execveat
        TASKFORGE_DENY_SYSCALL(__NR_execveat),
#endif
#ifdef __NR_fork
        TASKFORGE_DENY_SYSCALL(__NR_fork),
#endif
#ifdef __NR_vfork
        TASKFORGE_DENY_SYSCALL(__NR_vfork),
#endif
#ifdef __NR_clone
        TASKFORGE_DENY_SYSCALL(__NR_clone),
#endif
#ifdef __NR_clone3
        TASKFORGE_DENY_SYSCALL(__NR_clone3),
#endif
#ifdef __NR_kill
        TASKFORGE_DENY_SYSCALL(__NR_kill),
#endif
#ifdef __NR_tkill
        TASKFORGE_DENY_SYSCALL(__NR_tkill),
#endif
#ifdef __NR_tgkill
        TASKFORGE_DENY_SYSCALL(__NR_tgkill),
#endif
#ifdef __NR_ptrace
        TASKFORGE_DENY_SYSCALL(__NR_ptrace),
#endif
#ifdef __NR_process_vm_readv
        TASKFORGE_DENY_SYSCALL(__NR_process_vm_readv),
#endif
#ifdef __NR_process_vm_writev
        TASKFORGE_DENY_SYSCALL(__NR_process_vm_writev),
#endif
#ifdef __NR_pidfd_open
        TASKFORGE_DENY_SYSCALL(__NR_pidfd_open),
#endif
#ifdef __NR_pidfd_getfd
        TASKFORGE_DENY_SYSCALL(__NR_pidfd_getfd),
#endif
#ifdef __NR_pidfd_send_signal
        TASKFORGE_DENY_SYSCALL(__NR_pidfd_send_signal),
#endif

#ifdef __NR_socket
        TASKFORGE_DENY_SYSCALL(__NR_socket),
#endif
#ifdef __NR_socketpair
        TASKFORGE_DENY_SYSCALL(__NR_socketpair),
#endif
#ifdef __NR_connect
        TASKFORGE_DENY_SYSCALL(__NR_connect),
#endif
#ifdef __NR_bind
        TASKFORGE_DENY_SYSCALL(__NR_bind),
#endif
#ifdef __NR_listen
        TASKFORGE_DENY_SYSCALL(__NR_listen),
#endif
#ifdef __NR_accept
        TASKFORGE_DENY_SYSCALL(__NR_accept),
#endif
#ifdef __NR_accept4
        TASKFORGE_DENY_SYSCALL(__NR_accept4),
#endif
#ifdef __NR_sendto
        TASKFORGE_DENY_SYSCALL(__NR_sendto),
#endif
#ifdef __NR_sendmsg
        TASKFORGE_DENY_SYSCALL(__NR_sendmsg),
#endif
#ifdef __NR_recvfrom
        TASKFORGE_DENY_SYSCALL(__NR_recvfrom),
#endif
#ifdef __NR_recvmsg
        TASKFORGE_DENY_SYSCALL(__NR_recvmsg),
#endif
#ifdef __NR_shutdown
        TASKFORGE_DENY_SYSCALL(__NR_shutdown),
#endif

#ifdef __NR_mount
        TASKFORGE_DENY_SYSCALL(__NR_mount),
#endif
#ifdef __NR_umount2
        TASKFORGE_DENY_SYSCALL(__NR_umount2),
#endif
#ifdef __NR_pivot_root
        TASKFORGE_DENY_SYSCALL(__NR_pivot_root),
#endif
#ifdef __NR_chroot
        TASKFORGE_DENY_SYSCALL(__NR_chroot),
#endif
#ifdef __NR_unshare
        TASKFORGE_DENY_SYSCALL(__NR_unshare),
#endif
#ifdef __NR_setns
        TASKFORGE_DENY_SYSCALL(__NR_setns),
#endif
#ifdef __NR_sethostname
        TASKFORGE_DENY_SYSCALL(__NR_sethostname),
#endif
#ifdef __NR_setdomainname
        TASKFORGE_DENY_SYSCALL(__NR_setdomainname),
#endif

#ifdef __NR_bpf
        TASKFORGE_DENY_SYSCALL(__NR_bpf),
#endif
#ifdef __NR_perf_event_open
        TASKFORGE_DENY_SYSCALL(__NR_perf_event_open),
#endif
#ifdef __NR_userfaultfd
        TASKFORGE_DENY_SYSCALL(__NR_userfaultfd),
#endif
#ifdef __NR_io_uring_setup
        TASKFORGE_DENY_SYSCALL(__NR_io_uring_setup),
#endif
#ifdef __NR_io_uring_enter
        TASKFORGE_DENY_SYSCALL(__NR_io_uring_enter),
#endif
#ifdef __NR_io_uring_register
        TASKFORGE_DENY_SYSCALL(__NR_io_uring_register),
#endif
#ifdef __NR_keyctl
        TASKFORGE_DENY_SYSCALL(__NR_keyctl),
#endif
#ifdef __NR_add_key
        TASKFORGE_DENY_SYSCALL(__NR_add_key),
#endif
#ifdef __NR_request_key
        TASKFORGE_DENY_SYSCALL(__NR_request_key),
#endif
#ifdef __NR_kexec_load
        TASKFORGE_DENY_SYSCALL(__NR_kexec_load),
#endif
#ifdef __NR_kexec_file_load
        TASKFORGE_DENY_SYSCALL(__NR_kexec_file_load),
#endif
#ifdef __NR_init_module
        TASKFORGE_DENY_SYSCALL(__NR_init_module),
#endif
#ifdef __NR_finit_module
        TASKFORGE_DENY_SYSCALL(__NR_finit_module),
#endif
#ifdef __NR_delete_module
        TASKFORGE_DENY_SYSCALL(__NR_delete_module),
#endif
#ifdef __NR_reboot
        TASKFORGE_DENY_SYSCALL(__NR_reboot),
#endif
#ifdef __NR_swapon
        TASKFORGE_DENY_SYSCALL(__NR_swapon),
#endif
#ifdef __NR_swapoff
        TASKFORGE_DENY_SYSCALL(__NR_swapoff),
#endif
#ifdef __NR_open_by_handle_at
        TASKFORGE_DENY_SYSCALL(__NR_open_by_handle_at),
#endif
#ifdef __NR_name_to_handle_at
        TASKFORGE_DENY_SYSCALL(__NR_name_to_handle_at),
#endif

#ifdef __NR_memfd_create
        TASKFORGE_DENY_SYSCALL(__NR_memfd_create),
#endif
#ifdef __NR_mprotect
        TASKFORGE_DENY_SYSCALL(__NR_mprotect),
#endif
#ifdef __NR_pkey_mprotect
        TASKFORGE_DENY_SYSCALL(__NR_pkey_mprotect),
#endif
#ifdef __NR_mmap
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, __NR_mmap, 0, 3),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, args[2])),
        BPF_JUMP(BPF_JMP | BPF_JSET | BPF_K, PROT_EXEC, 0, 1),
        BPF_STMT(BPF_RET | BPF_K, TASKFORGE_DENY_ACTION),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, nr)),
#endif

#ifdef __NR_prctl
        TASKFORGE_DENY_SYSCALL(__NR_prctl),
#endif
#ifdef __NR_seccomp
        TASKFORGE_DENY_SYSCALL(__NR_seccomp),
#endif

        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ALLOW),
    };
    struct sock_fprog program = {
        .len = (unsigned short)(sizeof(filter) / sizeof(filter[0])),
        .filter = filter,
    };

#ifdef __NR_prctl
    if (taskforge_raw_syscall6(__NR_prctl, PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0, 0) != 0) {
        taskforge_guard_fail();
    }
    if (taskforge_raw_syscall6(__NR_prctl, PR_SET_SECCOMP, SECCOMP_MODE_FILTER,
                               (long)(uintptr_t)&program, 0, 0, 0) != 0) {
        taskforge_guard_fail();
    }
#else
    taskforge_guard_fail();
#endif
}
