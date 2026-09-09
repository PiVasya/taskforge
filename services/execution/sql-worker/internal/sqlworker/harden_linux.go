//go:build linux

package sqlworker

import (
	"fmt"
	"os"
	"runtime"
	"runtime/debug"
	"syscall"
	"unsafe"
)

// InstallIsolation seals every existing OS thread, including native driver and Go
// runtime threads. A thread-local prctl(SECCOMP) filter would NOT be sufficient.
// Connections and SQLite files must already be open. New files/connections and
// process creation are then denied; Go may still create threads in its own process.
func InstallIsolation(timeoutMS int) error {
	runtime.GOMAXPROCS(1)
	debug.SetMemoryLimit(64 << 20)
	debug.SetMaxThreads(16)
	_ = os.WriteFile("/proc/self/oom_score_adj", []byte("900"), 0600)
	for resource, limit := range map[int]syscall.Rlimit{
		syscall.RLIMIT_CORE:   {Cur: 0, Max: 0},
		syscall.RLIMIT_FSIZE:  {Cur: 64 << 20, Max: 64 << 20},
		syscall.RLIMIT_CPU:    {Cur: uint64((timeoutMS+999)/1000 + 1), Max: uint64((timeoutMS+999)/1000 + 2)},
		syscall.RLIMIT_NOFILE: {Cur: 64, Max: 64},
	} {
		if err := syscall.Setrlimit(resource, &limit); err != nil {
			return fmt.Errorf("resource isolation: %w", err)
		}
	}
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	if _, _, err := syscall.RawSyscall6(syscall.SYS_PRCTL, 38, 1, 0, 0, 0, 0); err != 0 {
		return fmt.Errorf("no_new_privs: %w", err)
	}
	if _, _, err := syscall.RawSyscall6(syscall.SYS_PRCTL, 4, 0, 0, 0, 0, 0); err != 0 {
		return fmt.Errorf("dumpable: %w", err)
	}
	filter, err := isolationFilter(os.Getpid())
	if err != nil {
		return err
	}
	program := syscall.SockFprog{Len: uint16(len(filter)), Filter: &filter[0]}
	// SECCOMP_SET_MODE_FILTER=1, SECCOMP_FILTER_FLAG_TSYNC=1.
	result, _, errno := syscall.RawSyscall(seccompNumber, 1, 1, uintptr(unsafe.Pointer(&program)))
	runtime.KeepAlive(filter)
	if errno != 0 || result != 0 {
		return fmt.Errorf("all-thread seccomp installation failed: %d/%v", result, errno)
	}
	return nil
}

func isolationFilter(pid int) ([]syscall.SockFilter, error) {
	if auditArchitecture == 0 {
		return nil, fmt.Errorf("unsupported seccomp architecture")
	}
	const load = 0x20
	const jumpEqual = 0x15
	const and = 0x54
	const ret = 0x06
	const allow = 0x7fff0000
	const deny = 0x00050001
	const kill = 0x80000000
	st := func(code uint16, k uint32) syscall.SockFilter { return syscall.SockFilter{Code: code, K: k} }
	eq := func(k uint32, yes, no uint8) syscall.SockFilter {
		return syscall.SockFilter{Code: jumpEqual, K: k, Jt: yes, Jf: no}
	}
	filters := []syscall.SockFilter{st(load, 4), eq(auditArchitecture, 1, 0), st(ret, kill), st(load, 0)}
	// glibc may try clone3 first. ENOSYS makes it use inspectable legacy clone.
	filters = append(filters, eq(435, 0, 1), st(ret, 0x00050026))
	// CLONE_THREAD and CLONE_VM are required: clone may create only a thread sharing our VM. Namespace, process
	// creation, ptrace, vfork and nonzero exit signals are rejected.
	const threadFlags = uint32(0x00000100 | 0x00000200 | 0x00000400 | 0x00000800 | 0x00010000 | 0x00040000 | 0x00080000 | 0x00100000 | 0x00200000 | 0x01000000)
	filters = append(filters, eq(cloneNumber, 0, 7), st(load, 16), st(and, ^threadFlags), eq(0, 0, 3), st(load, 16), st(and, 0x10100), eq(0x10100, 1, 0), st(ret, deny), st(ret, allow))
	// The false branch above must skip the entire clone-only block.
	filters[6].Jf = 8
	// Async runtime preemption may signal ONLY this process's own threads.
	filters = append(filters, eq(tgkillNumber, 0, 4), st(load, 16), eq(uint32(pid), 1, 0), st(ret, deny), st(ret, allow))
	for _, nr := range allowedSyscalls {
		filters = append(filters, eq(nr, 0, 1), st(ret, allow))
	}
	filters = append(filters, st(ret, deny))
	return filters, nil
}
