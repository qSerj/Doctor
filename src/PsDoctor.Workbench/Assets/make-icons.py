# Собрать значки пульта из исходной картинки workbench-source.webp (фон уже прозрачный).
#   python src/PsDoctor.Workbench/Assets/make-icons.py      нужен Pillow
# workbench.ico       — настройщик целиком: exe и ярлык.
# workbench-head.ico  — только голова: заголовок окна и панель задач, где целая фигура при 16–32 точках сливается в пятно.
# Способ тот же, что у значков Doctor в src/PsDoctor.App/Assets/make-icons.py.
import pathlib

from PIL import Image, ImageDraw

assets = pathlib.Path(__file__).resolve().parent
source = Image.open(assets / "workbench-source.webp").convert("RGBA")

# Голова: каска с фонарём, очки, борода. Рамка подобрана по исходнику 1254×1254.
HEAD = (262, 30, 1012, 745)
# Гаечный ключ, попавший в рамку головы слева под ухом; координаты внутри рамки.
WRENCH = (0, 505, 47, 715)


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


head = source.crop(HEAD)
alpha = head.getchannel("A")
ImageDraw.Draw(alpha).rectangle(WRENCH, fill=0)
head.putalpha(alpha)

save_ico(square(source), "workbench.ico", [16, 20, 24, 32, 40, 48, 64, 256])
save_ico(square(head), "workbench-head.ico", [16, 20, 24, 32, 40, 48, 64])
