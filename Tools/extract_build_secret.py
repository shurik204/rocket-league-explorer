#!/usr/bin/env python3
"""Extract Rocket League's PsyNet build secret from RocketLeague.exe.

UnrealScript exposes it as `native static final function string
PsyNet_X.GetBuildSecret()`, so the value is compiled into the executable:

  * the native registration table holds `"UPsyNet_XexecGetBuildSecret"`
    immediately followed by a pointer to the DECLARE_FUNCTION thunk;
  * the thunk's first direct `call` jumps to the real implementation;
  * the implementation XOR-decodes a blob with a 4-byte rolling key that is
    stored directly after the ciphertext.

No addresses are hard-coded, so this keeps working after a game update:

    python extract_build_secret.py <path to RocketLeague.exe>

Requires: pefile, capstone (pip install pefile capstone)
"""
import struct
import sys

import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86 import X86_OP_IMM

NATIVE_NAME = b"UPsyNet_XexecGetBuildSecret\x00"


def main(path):
    pe = pefile.PE(path, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    data = open(path, "rb").read()
    sections = [
        (s.Name.decode().rstrip("\x00"), s.VirtualAddress,
         max(s.Misc_VirtualSize, s.SizeOfRawData), s.PointerToRawData)
        for s in pe.sections
    ]

    def va_to_file(va):
        rva = va - base
        for _, sva, size, raw in sections:
            if sva <= rva < sva + size:
                return raw + (rva - sva)
        return None

    def file_to_va(off):
        for _, sva, size, raw in sections:
            if raw <= off < raw + size:
                return base + sva + (off - raw)
        return None

    def qword(va):
        return struct.unpack_from("<Q", data, va_to_file(va))[0]

    # 1. Locate the native name and the {name, func} pair that references it.
    str_off = data.find(NATIVE_NAME)
    if str_off < 0:
        raise SystemExit("native name not found; is this a Rocket League build?")
    name_va = file_to_va(str_off)

    entry = None
    pattern = struct.pack("<Q", name_va)
    off = 0
    while True:
        off = data.find(pattern, off)
        if off < 0:
            break
        func_va = file_to_va(off + 8)
        if func_va is not None:
            target = qword(func_va)
            if va_to_file(target) is not None:
                entry = func_va
                break
        off += 1
    if entry is None:
        raise SystemExit("native registration entry not found")

    thunk = qword(entry)

    # 2. The thunk's first direct call is the real implementation.
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    md.detail = True
    real = None
    thunk_off = va_to_file(thunk)
    for ins in md.disasm(data[thunk_off:thunk_off + 256], thunk):
        if ins.mnemonic == "call" and ins.operands and ins.operands[0].type == X86_OP_IMM:
            dest = ins.operands[0].imm
            if va_to_file(dest) is not None and dest != thunk:
                real = dest
                break
        if ins.mnemonic == "ret":
            break
    if real is None:
        raise SystemExit("implementation not found")

    # 3. The implementation bounds the blob with lea r8,[blob] / lea r9,[blob_end].
    blob = end = None
    real_off = va_to_file(real)
    for ins in md.disasm(data[real_off:real_off + 512], real):
        if ins.mnemonic == "lea":
            if ins.op_str.startswith("r8, [rip"):
                blob = ins.address + ins.size + ins.operands[1].mem.disp
            elif ins.op_str.startswith("r9, [rip"):
                end = ins.address + ins.size + ins.operands[1].mem.disp
        if blob is not None and end is not None:
            break
    if blob is None or end is None:
        raise SystemExit("blob bounds not found")

    length = end - blob
    if not 0 < length <= 0x400:
        raise SystemExit(f"unexpected blob size {length}")

    # 4. The blob is <length> cipher bytes followed by a 4-byte rolling key.
    off = va_to_file(blob)
    cipher = data[off:off + length]
    key = data[off + length:off + length + 4]
    plain = bytes(cipher[i] ^ key[i & 3] for i in range(length))

    print(f"thunk          {thunk:#x}")
    print(f"implementation {real:#x}")
    print(f"blob           {blob:#x} ({length} bytes), key {key.hex()}")
    print(f"build secret   {plain.decode('ascii', 'replace')}")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "RocketLeague.exe")
