# Собрать значки Doctor из исходной картинки doctor-source.webp (фон уже прозрачный).
#   python src/PsDoctor.App/Assets/make-icons.py      нужен Pillow
# doctor.ico       — доктор целиком: exe, ярлык на рабочем столе и в «Пуске».
# doctor-tray.ico  — только голова: трей и заголовок окна, где целый доктор при 16–32 точках сливается в пятно.
import pathlib

from PIL import Image

assets = pathlib.Path(__file__).resolve().parent
source = Image.open(assets / "doctor-source.webp").convert("RGBA")

# Голова: шапочка, очки, борода. Рамка подобрана по исходнику 1254×1254.
HEAD = (236, 7, 1030, 745)


def square(image: Image.Image, margin: float = 0.02) -> Image.Image:
    """Обрезать по непрозрачному и уложить в квадрат с небольшим полем."""
    image = image.crop(image.getchannel("A").getbbox())
    side = round(max(image.size) * (1 + 2 * margin))
    canvas = Image.new("RGBA", (side, side))
    canvas.paste(image, ((side - image.width) // 2, (side - image.height) // 2))
    return canvas


def save_ico(image: Image.Image, name: str, sizes: list[int]) -> None:
    # Каждый размер уменьшается из большого отдельно: так мелкие не мылятся цепочкой пересжатий.
    frames = [image.resize((s, s), Image.LANCZOS) for s in sizes]
    frames[-1].save(assets / name, sizes=[(s, s) for s in sizes], append_images=frames[:-1])


full = square(source)
head = square(source.crop(HEAD))
save_ico(full, "doctor.ico", [16, 20, 24, 32, 40, 48, 64, 256])
save_ico(head, "doctor-tray.ico", [16, 20, 24, 32, 40, 48, 64])
