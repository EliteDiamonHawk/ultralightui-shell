export const clamp = (value, minimum, maximum) => Math.min(maximum, Math.max(minimum, value));

export const formatPercent = (value) => Number(value).toFixed(1) + "%";

export const formatBytes = (value) => {
  const numeric = Number(value);
  if (!Number.isFinite(numeric)) return "n/a";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let scaled = numeric;
  let unit = 0;
  while (scaled >= 1024 && unit < units.length - 1) {
    scaled /= 1024;
    unit += 1;
  }
  return scaled.toFixed(unit > 1 ? 1 : 0) + " " + units[unit];
};

export const formatDuration = (milliseconds, syntax = "HH:mm:ss") => {
  const totalSeconds = Math.max(0, Math.floor(Number(milliseconds) / 1000));
  const hours = Math.floor(totalSeconds / 3600);
  const totalMinutes = Math.floor(totalSeconds / 60);
  const minutes = totalMinutes % 60;
  const seconds = totalSeconds % 60;
  const values = {
    MM: String(totalMinutes).padStart(2, "0"),
    HH: String(hours).padStart(2, "0"),
    M: String(totalMinutes),
    H: String(hours),
    mm: String(minutes).padStart(2, "0"),
    m: String(minutes),
    ss: String(seconds).padStart(2, "0"),
    s: String(seconds),
  };
  return String(syntax ?? "HH:mm:ss").replace(/MM|HH|mm|ss|M|H|m|s/g, (token) => values[token]);
};
