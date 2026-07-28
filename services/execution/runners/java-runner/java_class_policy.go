package main

import (
	"encoding/binary"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"strings"
)

const (
	maxJavaClassFiles      = 256
	maxJavaClassFileBytes  = 16 << 20
	maxJavaClassTotalBytes = 32 << 20
)

type javaCPEntry struct {
	tag  byte
	a    uint16
	b    uint16
	text string
}

func verifyJavaClassFiles(root string) error {
	count := 0
	total := int64(0)
	return filepath.WalkDir(root, func(path string, entry fs.DirEntry, walkErr error) error {
		if walkErr != nil {
			return walkErr
		}
		if entry.Type()&os.ModeSymlink != 0 {
			return errors.New("symbolic links are not allowed in Java compiler output")
		}
		if entry.IsDir() || !strings.HasSuffix(strings.ToLower(entry.Name()), ".class") {
			return nil
		}
		count++
		if count > maxJavaClassFiles {
			return errors.New("too many Java class files")
		}
		info, err := entry.Info()
		if err != nil {
			return err
		}
		if !info.Mode().IsRegular() || info.Size() <= 0 || info.Size() > maxJavaClassFileBytes {
			return errors.New("invalid Java class file")
		}
		total += info.Size()
		if total > maxJavaClassTotalBytes {
			return errors.New("Java class output is too large")
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		return verifyJavaClassFile(data)
	})
}

func verifyJavaClassFile(data []byte) error {
	if len(data) < 10 || binary.BigEndian.Uint32(data[:4]) != 0xCAFEBABE {
		return errors.New("invalid Java class file header")
	}
	index := 8
	cpCount := int(binary.BigEndian.Uint16(data[index : index+2]))
	index += 2
	if cpCount < 1 || cpCount > 65535 {
		return errors.New("invalid Java constant pool")
	}
	cp := make([]javaCPEntry, cpCount)
	for i := 1; i < cpCount; i++ {
		if index >= len(data) {
			return errors.New("truncated Java constant pool")
		}
		tag := data[index]
		index++
		cp[i].tag = tag
		switch tag {
		case 1: // Utf8
			if index+2 > len(data) {
				return errors.New("truncated Java UTF8 constant")
			}
			length := int(binary.BigEndian.Uint16(data[index : index+2]))
			index += 2
			if length > 65535 || index+length > len(data) {
				return errors.New("truncated Java UTF8 constant")
			}
			cp[i].text = string(data[index : index+length])
			index += length
		case 3, 4: // Integer, Float
			if index+4 > len(data) {
				return errors.New("truncated Java constant")
			}
			index += 4
		case 5, 6: // Long, Double (two slots)
			if index+8 > len(data) {
				return errors.New("truncated Java constant")
			}
			index += 8
			i++
		case 7, 8, 16, 19, 20: // Class, String, MethodType, Module, Package
			if index+2 > len(data) {
				return errors.New("truncated Java constant")
			}
			cp[i].a = binary.BigEndian.Uint16(data[index : index+2])
			index += 2
		case 9, 10, 11, 12, 17, 18: // refs, NameAndType, Dynamic
			if index+4 > len(data) {
				return errors.New("truncated Java constant")
			}
			cp[i].a = binary.BigEndian.Uint16(data[index : index+2])
			cp[i].b = binary.BigEndian.Uint16(data[index+2 : index+4])
			index += 4
		case 15: // MethodHandle
			if index+3 > len(data) {
				return errors.New("truncated Java method handle")
			}
			cp[i].a = uint16(data[index])
			cp[i].b = binary.BigEndian.Uint16(data[index+1 : index+3])
			index += 3
		default:
			return fmt.Errorf("unsupported Java constant pool tag %d", tag)
		}
	}

	utf8 := func(i uint16) string {
		if int(i) <= 0 || int(i) >= len(cp) || cp[i].tag != 1 {
			return ""
		}
		return cp[i].text
	}
	className := func(i uint16) string {
		if int(i) <= 0 || int(i) >= len(cp) || cp[i].tag != 7 {
			return ""
		}
		return utf8(cp[i].a)
	}
	nameAndType := func(i uint16) (string, string) {
		if int(i) <= 0 || int(i) >= len(cp) || cp[i].tag != 12 {
			return "", ""
		}
		return utf8(cp[i].a), utf8(cp[i].b)
	}

	for i := 1; i < len(cp); i++ {
		entry := cp[i]
		switch entry.tag {
		case 7:
			if forbiddenJavaClass(className(uint16(i))) {
				return errors.New("forbidden Java class reference")
			}
		case 8:
			value := strings.ToLower(utf8(entry.a))
			if containsRestrictedPath(value) {
				return errors.New("forbidden Java path reference")
			}
		case 10, 11:
			owner := className(entry.a)
			name, _ := nameAndType(entry.b)
			if forbiddenJavaMethod(owner, name) {
				return errors.New("forbidden Java method reference")
			}
		}
	}
	return nil
}

func forbiddenJavaClass(name string) bool {
	for _, prefix := range []string{
		"java/lang/Process", "java/lang/ProcessBuilder", "java/lang/Runtime",
		"java/io/File", "java/io/RandomAccessFile", "java/io/ObjectInputStream", "java/io/ObjectOutputStream",
		"java/io/Externalizable", "java/nio/file/", "java/net/", "java/rmi/", "javax/rmi/",
		"java/lang/reflect/", "java/lang/invoke/MethodHandles", "java/lang/ClassLoader",
		"java/lang/ModuleLayer", "java/lang/instrument/", "java/lang/management/", "java/lang/Compiler",
		"java/security/", "javax/script/", "javax/tools/", "javax/naming/", "javax/management/",
		"java/sql/", "javax/sql/", "java/beans/", "java/util/ServiceLoader", "java/util/jar/",
		"sun/", "com/sun/", "jdk/internal/",
	} {
		if strings.HasPrefix(name, prefix) {
			return true
		}
	}
	return false
}

func forbiddenJavaMethod(owner, name string) bool {
	if owner == "java/lang/System" {
		switch name {
		case "load", "loadLibrary", "getenv", "getProperties", "getProperty", "setProperty", "clearProperty", "exit", "setSecurityManager":
			return true
		}
	}
	if owner == "java/lang/Class" {
		switch name {
		case "forName", "getClassLoader", "getClasses", "getConstructors", "getDeclaredClasses",
			"getDeclaredConstructor", "getDeclaredConstructors", "getDeclaredField", "getDeclaredFields",
			"getDeclaredMethod", "getDeclaredMethods", "getField", "getFields", "getMethod", "getMethods",
			"getProtectionDomain", "getResource", "getResourceAsStream", "newInstance":
			return true
		}
	}
	return false
}

func containsRestrictedPath(value string) bool {
	for _, needle := range []string{"/proc/", "/sys/", "/dev/", "/etc/", "/run/", "../", "..\\"} {
		if strings.Contains(value, needle) {
			return true
		}
	}
	return false
}
