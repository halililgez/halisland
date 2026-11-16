/**
 * Kapaktan adanın vurgu rengini çıkarır.
 * Görsel küçük bir kanvasa çizilir; doygun pikseller daha ağır sayılarak
 * ortalama alınır, sonra renk koyu adada okunur kalacak şekilde
 * parlaklık/doygunluk aralığına çekilir.
 */
export async function accentFromImage(src: string): Promise<string | undefined> {
  const img = new Image();
  img.src = src;
  try {
    await img.decode();
  } catch {
    return undefined;
  }

  const size = 24;
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = size;
  const ctx = canvas.getContext("2d", { willReadFrequently: true });
  if (!ctx) return undefined;
  ctx.drawImage(img, 0, 0, size, size);
  const { data } = ctx.getImageData(0, 0, size, size);

  let r = 0,
    g = 0,
    b = 0,
    weight = 0;
  for (let i = 0; i < data.length; i += 4) {
    const [pr, pg, pb] = [data[i], data[i + 1], data[i + 2]];
    const max = Math.max(pr, pg, pb);
    const min = Math.min(pr, pg, pb);
    const sat = max === 0 ? 0 : (max - min) / max;
    const w = 0.05 + sat * sat;
    r += pr * w;
    g += pg * w;
    b += pb * w;
    weight += w;
  }
  if (weight === 0) return undefined;

  const [h, s, l] = rgbToHsl(r / weight, g / weight, b / weight);
  const sat = Math.max(s, 0.45);
  const light = Math.min(Math.max(l, 0.5), 0.68);
  return `hsl(${Math.round(h * 360)} ${Math.round(sat * 100)}% ${Math.round(light * 100)}%)`;
}

export function rgbToHsl(r: number, g: number, b: number): [number, number, number] {
  r /= 255;
  g /= 255;
  b /= 255;
  const max = Math.max(r, g, b);
  const min = Math.min(r, g, b);
  const l = (max + min) / 2;
  if (max === min) return [0, 0, l];
  const d = max - min;
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
  let h: number;
  if (max === r) h = (g - b) / d + (g < b ? 6 : 0);
  else if (max === g) h = (b - r) / d + 2;
  else h = (r - g) / d + 4;
  return [h / 6, s, l];
}
