# colors

A MISAML mod for Mnemonimov that gives programs a programmable 256-colour pattern.

Normally the screen shows 256 shades between two fixed colours. With this mod, every pixel value (0 to 255) can be any RGB colour, set from your program with syscalls.

## Syscalls

| Word | Does |
|---|---|
| `SYS_GET_PALETTE_COLOR` | set one entry. `a0` = index, `a1` = `0x00RRGGBB` |
| `SYS_GET_PALETTE_COLOR` | read one entry. `a0` = index, returns `0x00RRGGBB` in `a0` |
| `SYS_COLOR_TO_INDEX` | find the closest existing entry to a colour. `a0` = `0x00RRGGBB`, returns the index in `a0` (never creates a new entry) |
| `SYS_SET_PALETTE_RANGE` | set many entries. `a0` = first index, `a1` = count, `a2` = address of `count` big-endian `0x00RRGGBB` words |
| `SYS_GET_PALETTE_RANGE` | read many entries into memory, same arguments |
| `SYS_RESET_PALETTE` | back to the game's default gradient |
| `SYS_CLEAR_PALETTE` | set entries 1 to 255 to black |
| `SYS_LOAD_PALETTE` | apply an embedded palette table. `a0` = table id |

Entry 0 is the background and is left alone by `SYS_CLEAR_PALETTE`. The palette resets to the default every time a program starts or stops.

The words are only available in the editor and assembler while this mod is enabled.

## Embedded palettes

A program can carry its own palettes. Embed this layout in the program's data:

```
"MISAML_PAL\0" marker
u8 id          table id
u8 first       first palette index
count * u32    0x00RRGGBB, big-endian
```

Apply a table `SYS_LOAD_PALETTE`.

## Example

Sets three palette entries in `_start`, then draws in `_draw`:

```
_start:
    mov a0, 1
    mov a1, 0x5BCEFA
    syscall SYS_SET_PALETTE_COLOR

    mov a0, 2
    mov a1, 0xF5A9B8
    syscall SYS_SET_PALETTE_COLOR

    mov a0, 3
    mov a1, 0xFFFFFF
    syscall SYS_SET_PALETTE_COLOR

    exit

_draw:
    mov a0, 0
    mov a1, 0
    mov a2, SCREEN_WIDTH
    mov a3, 48
    mov a4, 1
    syscall SYS_DRAW_RECT

    mov a0, 0
    mov a1, 48
    mov a2, SCREEN_WIDTH
    mov a3, 48
    mov a4, 2
    syscall SYS_DRAW_RECT

    mov a0, 0
    mov a1, 96
    mov a2, SCREEN_WIDTH
    mov a3, 48
    mov a4, 3
    syscall SYS_DRAW_RECT

    mov a0, 0
    mov a1, 144
    mov a2, SCREEN_WIDTH
    mov a3, 48
    mov a4, 2
    syscall SYS_DRAW_RECT

    mov a0, 0
    mov a1, 192
    mov a2, SCREEN_WIDTH
    mov a3, 48
    mov a4, 1
    syscall SYS_DRAW_RECT

    exit
```

Each pixel value is an index into the palette, so `a4` in `SYS_DRAW_RECT` picks which of your colours the band uses.

## Build

You need the .NET 8 SDK and a built copy of [MISAML](https://github.com/Mnemonimods/MISAML), so `MISAML.dll` exists. If yours lives somewhere else, change the `HintPath` in `color.csproj`. Then:

```sh
dotnet build -c Release
```

Copy `color.dll`, `mod.json` and `main.gd` into the mod folder.

## How it works

The mod changes the game's screen shader at runtime to look colours up in a 256x1 palette texture, using the shader the game already ships.