//go:build !linux

package sqlworker

import "fmt"

func InstallIsolation(int) error { return fmt.Errorf("SQL child isolation requires Linux") }
