"""Reproduce the sample's deliberately small, four-colour PNG fixtures (stdlib only)."""
import pathlib
import struct
import zlib

ROOT = pathlib.Path(__file__).resolve().parents[1] / 'Samples' / 'ComposedScrolling' / 'Assets'
ROOT.mkdir(parents=True, exist_ok=True)


def png(name, width, height, pixel):
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))
    data = b''.join(b'\0' + bytes(v for x in range(width) for v in pixel(x, y)) for y in range(height))
    (ROOT / name).write_bytes(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(data)) + chunk(b'IEND', b''))


green = [(224, 248, 208), (136, 192, 112), (52, 104, 86), (8, 24, 32)]
png('world.png', 256, 256, lambda x, y: green[3 if x % 8 == 0 or y % 8 == 0 else (((((x // 8 + 7 * (y // 8)) % 64) >> ((x % 8 + y % 8) % 6)) & 1) + x % 2)])
for name, colours in [('hero.png', [(255, 255, 255), (248, 208, 72), (208, 80, 32), (32, 24, 56)]),
                      ('enemy.png', [(255, 255, 255), (168, 208, 248), (64, 80, 184), (32, 24, 56)])]:
    png(name, 16, 16, lambda x, y, c=colours: c[0 if x in (0, 15) or y in (0, 15) else (3 if x % 7 == 0 or y % 7 == 0 else 1 + ((x // 8 + y // 8) % 2))])
