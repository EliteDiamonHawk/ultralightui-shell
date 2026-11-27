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

const DURATION_TOKENS = /MM|HH|mm|ss|M|H|m|s/g;
const isLetter = (value) => value !== undefined && /[A-Za-z]/.test(value);

const formatDurationSyntax = (syntax, values) => {
  const matches = [...syntax.matchAll(DURATION_TOKENS)];
  let output = "";
  let cursor = 0;
  matches.forEach((match, index) => {
    const start = match.index;
    const end = start + match[0].length;
    const previous = matches[index - 1];
    const next = matches[index + 1];
    const adjacentToPreviousToken = previous && previous.index + previous[0].length === start;
    const adjacentToNextToken = next && end === next.index;
    if ((isLetter(syntax[start - 1]) && !adjacentToPreviousToken) || (isLetter(syntax[end]) && !adjacentToNextToken)) return;
    output += syntax.slice(cursor, start) + values[match[0]];
    cursor = end;
  });
  return output + syntax.slice(cursor);
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
  return formatDurationSyntax(String(syntax ?? "HH:mm:ss"), values);
};
