#pragma once

#include "common.h"

/**
 * @file
 * Declares the loaders of the language-dependent gaiji image, the texture
 * holding the special symbols drawn inside the game's text, and of the
 * second font texture image, together with accessors for their buffers.
 */

/**
 *
 * Loads the gaiji image of the current language into the gaiji buffer.
 *
 * @mangled LoadGaijiImg__Fv
 * @address 0x2DD5D0
 * @size 0x100
 */
int LoadGaijiImg();

/**
 *
 * Returns the buffer holding the loaded gaiji image.
 *
 * @mangled GetGaijiImgPtr__Fv
 * @address 0x2DD6D0
 * @size 0x10
 */
u_char *GetGaijiImgPtr();

/**
 *
 * Loads the second font texture image, only when its buffer is set and the language is Japanese.
 *
 * @mangled LoadFontTex2Img__Fv
 * @address 0x2DD6E0
 * @size 0x50
 */
int LoadFontTex2Img();

/**
 *
 * Returns the buffer holding the second font texture image.
 *
 * @mangled GetFontTex2ImgPtr__Fv
 * @address 0x2DD730
 * @size 0x10
 */
u_char *GetFontTex2ImgPtr();
