"""Render a cumulative chart using the timestamps of currently active stars."""

import json
import os
import re
from collections import Counter
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.request import Request, urlopen


BEIJING = timezone(timedelta(hours=8))


def fetch_stars(api_url, repository, token):
    url = f"{api_url}/repos/{repository}/stargazers?per_page=100"
    headers = {
        "Accept": "application/vnd.github.star+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "NotchPeninsula-Star-History",
    }
    if token:
        headers["Authorization"] = f"Bearer {token}"

    stars = Counter()
    while url:
        with urlopen(Request(url, headers=headers), timeout=30) as response:
            for star in json.load(response):
                day = datetime.fromisoformat(star["starred_at"]).astimezone(BEIJING).date()
                stars[day] += 1
            next_page = re.search(r'<([^>]+)>; rel="next"', response.headers.get("Link", ""))
            url = next_page[1] if next_page else None
    return stars


def render_chart(stars, today):
    start = min(stars, default=today) - timedelta(days=1)
    end = max(max(stars, default=today), today)
    days = (end - start).days
    total = sum(stars.values())
    step = max(1, (total + 3) // 4)
    left, right, top, bottom = 78, 916, 103, 323

    def x(day):
        return left + (right - left) * (day - start).days / days

    def y(count):
        return bottom - (bottom - top) * count / (4 * step)

    grid = []
    for count in range(0, 4 * step + 1, step):
        row = y(count)
        grid.append(f'<path d="M {left} {row:g} H {right}" stroke="#dbe5ef"/>')
        grid.append(f'<text x="64" y="{row + 5:g}" text-anchor="end">{count:,}</text>')

    ticks = min(5, days + 1)
    for index in range(ticks):
        day = start + timedelta(days=round(days * index / (ticks - 1)))
        grid.append(f'<text x="{x(day):.2f}" y="349" text-anchor="middle">{day:%Y-%m-%d}</text>')

    remaining = 0
    path = [f"M {left} {bottom}"]
    for day, count in sorted(stars.items()):
        remaining += count
        path.append(f"H {x(day):.2f} V {y(remaining):.2f}")
    path.append(f"H {right}")
    line = " ".join(path)
    grid_markup = "\n    ".join(grid)
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="960" height="370" viewBox="0 0 960 370" role="img" aria-labelledby="title desc">
  <title id="title">NotchPeninsula Star History</title>
  <desc id="desc">Cumulative currently active GitHub stars through {end}. Total: {total}.</desc>
  <rect width="960" height="370" rx="18" fill="#f4f8fc"/>
  <g font-family="system-ui, sans-serif">
    <text x="32" y="46" fill="#142b46" font-weight="700" font-size="24">Star History</text>
    <text x="32" y="73" fill="#4a617a" font-size="15">NotchPeninsula · Cumulative stars (UTC+8)</text>
    <text x="916" y="51" text-anchor="end" fill="#1556a0" font-weight="700" font-size="21">{total:,} stars</text>
    <g fill="#4a617a" font-size="13">
    {grid_markup}
    </g>
  </g>
  <path d="{line} V {bottom} H {left} Z" fill="#d9eafb"/>
  <path d="{line}" fill="none" stroke="#2368b5" stroke-width="2.5" stroke-linejoin="round"/>
  <circle cx="{right}" cy="{y(total):.2f}" r="4" fill="#2368b5"/>
</svg>
'''


if __name__ == "__main__":
    stars = fetch_stars(
        os.environ.get("GITHUB_API_URL", "https://api.github.com"),
        os.environ["GITHUB_REPOSITORY"],
        os.environ.get("GH_TOKEN"),
    )
    output = Path("docs/star-history.svg")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(render_chart(stars, datetime.now(BEIJING).date()), encoding="utf-8", newline="\n")
