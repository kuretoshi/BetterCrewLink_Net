"""Announce a published .NET release using its channel's Discord webhook."""

import json
import os
import re
import sys
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlparse
from urllib.request import Request, urlopen


BETA_TAG = re.compile(r"v\d+\.\d+\.\d+-net-beta\.\d+\Z")
STABLE_TAG = re.compile(r"v\d+\.\d+\.\d+-net\.\d+\Z")
DISCORD_PATH = re.compile(r"/api/webhooks/\d+/[A-Za-z0-9._-]+\Z")
REPOSITORY = "kuretoshi/BetterCrewLink_Net"


def validate_release(release, channel, tag):
    pattern = BETA_TAG if channel == "beta" else STABLE_TAG
    if channel not in ("beta", "release") or not pattern.fullmatch(tag):
        raise ValueError("The release tag does not match its Discord channel")
    if (release.get("tag_name") != tag or release.get("draft")
            or release.get("prerelease") is not (channel == "beta")):
        raise ValueError("The requested tag is not a published release for this channel")
    expected_url = f"https://github.com/{REPOSITORY}/releases/tag/{tag}"
    if release.get("html_url") != expected_url:
        raise ValueError("The release URL does not match this repository and tag")
    return expected_url


def validate_webhook(value):
    parsed = urlparse(value)
    if (parsed.scheme != "https" or parsed.hostname not in ("discord.com", "discordapp.com")
            or parsed.port is not None or parsed.username or parsed.password
            or parsed.query or parsed.fragment or not DISCORD_PATH.fullmatch(parsed.path)):
        raise ValueError("DISCORD_WEBHOOK_URL must be a Discord incoming webhook URL without /github")
    return value


def main(channel, tag):
    pattern = BETA_TAG if channel == "beta" else STABLE_TAG
    if channel not in ("beta", "release") or not pattern.fullmatch(tag):
        raise ValueError("The release tag does not match its Discord channel")
    webhook = validate_webhook(os.environ.get("DISCORD_WEBHOOK_URL", ""))
    token = os.environ.get("GITHUB_TOKEN")
    if not token:
        raise ValueError("GITHUB_TOKEN is missing")

    request = Request(
        f"https://api.github.com/repos/{REPOSITORY}/releases/tags/{quote(tag, safe='')}",
        headers={"Accept": "application/vnd.github+json", "Authorization": f"Bearer {token}"},
    )
    try:
        with urlopen(request, timeout=20) as response:
            release = json.load(response)
    except HTTPError as error:
        raise ValueError(f"GitHub release lookup failed: HTTP {error.code}") from None
    except URLError:
        raise ValueError("GitHub release lookup failed") from None

    release_url = validate_release(release, channel, tag)
    label = "プレリリース" if channel == "beta" else "通常リリース"
    message = (
        f"📢 **TanukiBCL.Net {tag[1:]} {label}公開**\n"
        f"変更点・ダウンロード：{release_url}\n"
        "Windows x64向けです。不具合があればGitHub Issuesへお知らせください。"
    )
    payload = json.dumps({"content": message, "allowed_mentions": {"parse": []}}).encode("utf-8")
    request = Request(
        f"{webhook}?wait=true", data=payload,
        headers={"Content-Type": "application/json"}, method="POST",
    )
    try:
        with urlopen(request, timeout=20) as response:
            sent = json.load(response)
    except HTTPError as error:
        raise ValueError(f"Discord webhook failed: HTTP {error.code}") from None
    except URLError:
        raise ValueError("Discord webhook failed") from None
    if not sent.get("id"):
        raise ValueError("Discord did not confirm the notification")
    print(f"Discord {channel} notification sent for {tag} (message ID {sent['id']})")


if __name__ == "__main__":
    try:
        if len(sys.argv) != 3:
            raise ValueError("Usage: notify-discord-release.py beta|release RELEASE_TAG")
        main(sys.argv[1], sys.argv[2])
    except ValueError as error:
        raise SystemExit(str(error)) from None
