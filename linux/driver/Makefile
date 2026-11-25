obj-m += ohman-wmi.o

KDIR ?= /lib/modules/$(shell uname -r)/build

# A kernel built with clang (CachyOS, and some Arch and Fedora flavours) wants its modules built the same way:
# with gcc the build stops on flags gcc does not know. The kernel's own configuration says which it was.
# LLVM given on the command line or by DKMS wins.
ifeq ($(origin LLVM),undefined)
ifeq ($(shell grep -s -q '^CONFIG_CC_IS_CLANG=y' $(KDIR)/.config && echo 1),1)
LLVM := 1
endif
endif
ifdef LLVM
KARGS += LLVM=$(LLVM)
endif

all:
	$(MAKE) -C $(KDIR) M=$(CURDIR) $(KARGS) modules

clean:
	$(MAKE) -C $(KDIR) M=$(CURDIR) $(KARGS) clean
