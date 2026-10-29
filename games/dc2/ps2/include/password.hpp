#pragma once

#include "common.h"

/**
 * @file
 * Declares the conversion between binary data and the text passwords that carry it.
 */

/**
 *
 * Writes binary data as password text, eleven characters per eight bytes,
 * and gives the text length, or -1 when a group does not convert back.
 *
 * @mangled ConvertBinToTxt__FPUciPc
 * @address 0x320F60
 * @size 0x140
 */
int ConvertBinToTxt(u8 *data, int size, char *text);

/**
 *
 * Reads password text back into binary data, eight bytes per eleven
 * characters, and gives the byte count, or -1 when the text is not valid.
 *
 * @mangled ConvertTxtToBin__FPcPUc
 * @address 0x3210A0
 * @size 0x120
 */
int ConvertTxtToBin(char *text, u8 *data);

/**
 *
 * Turns a block of data into password text tied to a key, such as an item's
 * name, and gives 1 on success or 0 when the size or text buffer is unsuitable.
 *
 * @mangled EncodePassword__FPUciPUciPci
 * @address 0x321640
 * @size 0x100
 */
int EncodePassword(u8 *data, int size, u8 *key, int key_size, char *text, int text_size);

/**
 *
 * Turns password text back into its block of data and gives 1 when its
 * checksum agrees with the data and the key, otherwise 0.
 *
 * @mangled DecodePassword__FPcPUciPUci
 * @address 0x321740
 * @size 0x110
 */
int DecodePassword(char *text, u8 *data, int size, u8 *key, int key_size);
