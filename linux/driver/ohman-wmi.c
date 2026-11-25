// SPDX-License-Identifier: GPL-2.0-or-later
/*
 * ohman-wmi - a passthrough to HP's BIOS mailbox for the Ohman app.
 *
 * HP OMEN and Victus firmware exposes one ACPI-WMI method that takes
 * {signature, command, command type, data} and answers {return code, data}.
 * The in-kernel hp-wmi driver uses it for the features it knows about; this
 * module hands the same call to user space, so that Ohman can speak to the
 * firmware on Linux with the bytes it already uses on Windows.
 *
 * It does not bind the WMI device and does not replace hp-wmi: both can be
 * loaded. The firmware's own method is serialized, so single calls from the
 * two cannot interleave; sequences can, which is why Ohman leaves hp-wmi's
 * fan control in automatic (pwm1_enable = 2) while it is running.
 *
 * It also reads and writes single bytes of the embedded controller, through
 * the kernel's own ec_read() and ec_write(). Those are the ACPI EC driver's
 * transactions, serialized with everything else the kernel does to the EC,
 * and they need neither the ec_sys module nor raw port access, so they work
 * under kernel lockdown. Which registers Ohman may write is decided in user
 * space (EcMap in src/Ec.cs, enforced again by ohman-helper); a few boards'
 * mailbox refuses fan levels and the EC is the only way to their fans.
 *
 * Interface: /dev/ohman-wmi, root only (mode 0600, and CAP_SYS_RAWIO on
 * every ioctl: the capability the kernel asks for raw port and MSR access,
 * which is what this amounts to, and the only one ohman-helper keeps).
 * Nothing here knows what any command or register means.
 *
 * The call path is the one hp-wmi.c uses (hp_wmi_perform_query), by
 * Matthew Garrett, Anssi Hannula and the platform-drivers-x86 contributors.
 */

#define pr_fmt(fmt) KBUILD_MODNAME ": " fmt

#include <linux/acpi.h>
#include <linux/fs.h>
#include <linux/kernel.h>
#include <linux/miscdevice.h>
#include <linux/module.h>
#include <linux/mutex.h>
#include <linux/slab.h>
#include <linux/types.h>
#include <linux/uaccess.h>
#include <linux/wmi.h>

#define OHMAN_BIOS_GUID		"5FB7F034-2C63-45E9-BE91-3D44E2C707E4"
#define OHMAN_SIGNATURE		0x55434553	/* "SECU" */
#define OHMAN_MAX_DATA		4096
#define OHMAN_MIN_INPUT		128		/* the AML reads past a shorter buffer on some boards */

/*
 * Bumped when an ioctl is added or a layout changes. 1 had the mailbox call
 * only (and no version ioctl: ENOTTY on OHMAN_WMI_IOC_VERSION means 1).
 */
#define OHMAN_WMI_VERSION	2

/*
 * The three structures and four request numbers below are the whole
 * interface, and src/linux/KernelModule.cs spells the same layouts and
 * numbers out by hand. Change one and change the other.
 */
struct ohman_wmi_call {
	__u32 command;		/* in:  e.g. 0x20008 */
	__u32 commandtype;	/* in:  e.g. 0x28 */
	__u32 insize;		/* in:  bytes of data[] that are input */
	__u32 outsize;		/* in:  0, 4, 128, 1024 or 4096 */
	__u32 rc;		/* out: the firmware's return code, 0 = accepted */
	__u32 outlen;		/* out: bytes of data[] the firmware actually returned */
	__u8  data[OHMAN_MAX_DATA];	/* in: input, out: reply */
};

struct ohman_ec_byte {
	__u8 reg;		/* in:  EC register, 0x00..0xFF */
	__u8 value;		/* read: out, write: in */
};

#define OHMAN_WMI_IOC_CALL	_IOWR('O', 1, struct ohman_wmi_call)
#define OHMAN_WMI_IOC_EC_READ	_IOWR('O', 2, struct ohman_ec_byte)
#define OHMAN_WMI_IOC_EC_WRITE	_IOW('O', 3, struct ohman_ec_byte)
#define OHMAN_WMI_IOC_VERSION	_IOR('O', 4, __u32)

struct bios_args {
	u32 signature;
	u32 command;
	u32 commandtype;
	u32 datasize;
	u8 data[];
};

struct bios_return {
	u32 sigpass;
	u32 return_code;
};

static DEFINE_MUTEX(ohman_lock);

/* The firmware has one method per reply size. */
static int method_for_outsize(u32 outsize)
{
	switch (outsize) {
	case 0:    return 1;
	case 4:    return 2;
	case 128:  return 3;
	case 1024: return 4;
	case 4096: return 5;
	default:   return -EINVAL;
	}
}

static int ohman_perform(struct ohman_wmi_call *c)
{
	struct acpi_buffer input, output = { ACPI_ALLOCATE_BUFFER, NULL };
	struct bios_return *ret;
	union acpi_object *obj;
	struct bios_args *args;
	size_t args_size, got;
	acpi_status status;
	int mid, err = 0;

	if (c->insize > OHMAN_MAX_DATA)
		return -EINVAL;
	mid = method_for_outsize(c->outsize);
	if (mid < 0)
		return mid;

	args_size = sizeof(*args) + max_t(u32, c->insize, OHMAN_MIN_INPUT);
	args = kzalloc(args_size, GFP_KERNEL);
	if (!args)
		return -ENOMEM;

	args->signature = OHMAN_SIGNATURE;
	args->command = c->command;
	args->commandtype = c->commandtype;
	args->datasize = c->insize;
	memcpy(args->data, c->data, c->insize);

	input.length = args_size;
	input.pointer = args;

	mutex_lock(&ohman_lock);
	status = wmi_evaluate_method(OHMAN_BIOS_GUID, 0, mid, &input, &output);
	mutex_unlock(&ohman_lock);
	kfree(args);

	if (ACPI_FAILURE(status)) {
		kfree(output.pointer);
		return -EIO;
	}

	obj = output.pointer;
	if (!obj || obj->type != ACPI_TYPE_BUFFER || !obj->buffer.pointer ||
	    obj->buffer.length < sizeof(*ret)) {
		err = -ENODATA;
		goto out;
	}

	ret = (struct bios_return *)obj->buffer.pointer;
	c->rc = ret->return_code;
	c->outlen = 0;
	memset(c->data, 0, sizeof(c->data));
	if (c->rc == 0 && c->outsize) {
		got = min_t(size_t, c->outsize, obj->buffer.length - sizeof(*ret));
		memcpy(c->data, obj->buffer.pointer + sizeof(*ret), got);
		c->outlen = got;
	}
out:
	kfree(output.pointer);
	return err;
}

static long ohman_ec_ioctl(unsigned int cmd, void __user *arg)
{
	struct ohman_ec_byte b;
	int err;

	if (copy_from_user(&b, arg, sizeof(b)))
		return -EFAULT;
	if (cmd == OHMAN_WMI_IOC_EC_WRITE)
		return ec_write(b.reg, b.value);

	err = ec_read(b.reg, &b.value);
	if (err)
		return err;
	return copy_to_user(arg, &b, sizeof(b)) ? -EFAULT : 0;
}

static long ohman_ioctl(struct file *file, unsigned int cmd, unsigned long arg)
{
	struct ohman_wmi_call *c;
	__u32 version = OHMAN_WMI_VERSION;
	long err;

	switch (cmd) {
	case OHMAN_WMI_IOC_CALL:
	case OHMAN_WMI_IOC_EC_READ:
	case OHMAN_WMI_IOC_EC_WRITE:
	case OHMAN_WMI_IOC_VERSION:
		break;
	default:
		return -ENOTTY;
	}
	if (!capable(CAP_SYS_RAWIO))
		return -EPERM;

	if (cmd == OHMAN_WMI_IOC_VERSION)
		return put_user(version, (__u32 __user *)arg);
	if (cmd != OHMAN_WMI_IOC_CALL)
		return ohman_ec_ioctl(cmd, (void __user *)arg);

	c = memdup_user((void __user *)arg, sizeof(*c));
	if (IS_ERR(c))
		return PTR_ERR(c);

	err = ohman_perform(c);
	if (!err && copy_to_user((void __user *)arg, c, sizeof(*c)))
		err = -EFAULT;

	kfree(c);
	return err;
}

static const struct file_operations ohman_fops = {
	.owner		= THIS_MODULE,
	.unlocked_ioctl	= ohman_ioctl,
	.compat_ioctl	= compat_ptr_ioctl,
};

static struct miscdevice ohman_misc = {
	.minor	= MISC_DYNAMIC_MINOR,
	.name	= "ohman-wmi",
	.fops	= &ohman_fops,
	.mode	= 0600,
};

static int __init ohman_wmi_init(void)
{
	if (!wmi_has_guid(OHMAN_BIOS_GUID)) {
		pr_info("no HP BIOS mailbox on this machine\n");
		return -ENODEV;
	}
	return misc_register(&ohman_misc);
}

static void __exit ohman_wmi_exit(void)
{
	misc_deregister(&ohman_misc);
}

module_init(ohman_wmi_init);
module_exit(ohman_wmi_exit);

MODULE_AUTHOR("Ohman contributors");
MODULE_DESCRIPTION("Passthrough to the HP OMEN/Victus BIOS mailbox and EC bytes for Ohman");
MODULE_VERSION("0.2");
MODULE_LICENSE("GPL");
