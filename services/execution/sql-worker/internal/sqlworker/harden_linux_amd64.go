package sqlworker

const seccompNumber = 317
const auditArchitecture = 0xc000003e
const cloneNumber = 56
const tgkillNumber = 234

// No open/connect/socket/fork/exec, filesystem mutation by pathname, process
// inspection, privilege changes, io_uring, ptrace or mount operations.
var allowedSyscalls = []uint32{
	0, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 23, 24, 25, 28,
	35, 39, 44, 45, 46, 47, 48, 51, 52, 54, 55, 60, 63, 72, 73, 74, 75, 77, 96, 97, 98, 99, 100, 102, 104, 107, 108,
	110, 111, 124, 130, 131, 137, 138, 158, 186, 201, 202, 204, 218, 219, 228, 229, 230, 231, 232, 233, 262, 270, 271,
	273, 274, 281, 284, 290, 291, 292, 293, 295, 296, 318, 324, 334, 436, 441,
}
