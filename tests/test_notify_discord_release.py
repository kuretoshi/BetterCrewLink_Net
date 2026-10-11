"""Offline checks for both release-channel Discord webhook senders."""

import importlib.util
import io
import json
import os
from pathlib import Path
import unittest
from unittest import mock


SOURCE = Path(__file__).resolve().parents[1] / "tools" / "notify-discord-release.py"
SPEC = importlib.util.spec_from_file_location("notify_discord_release", SOURCE)
notifier = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(notifier)
TAG = "v3.2.24-net-beta.2"
RELEASE = {
    "tag_name": TAG,
    "draft": False,
    "prerelease": True,
    "html_url": f"https://github.com/{notifier.REPOSITORY}/releases/tag/{TAG}",
}
STABLE_TAG = "v3.2.24-net.0"
STABLE_RELEASE = {
    **RELEASE,
    "tag_name": STABLE_TAG,
    "prerelease": False,
    "html_url": f"https://github.com/{notifier.REPOSITORY}/releases/tag/{STABLE_TAG}",
}
WEBHOOK = "https://discord.com/api/webhooks/123456789/fake_test_token"


class DiscordReleaseTests(unittest.TestCase):
    def test_beta_and_stable_cannot_cross_channels(self):
        self.assertEqual(notifier.validate_release(RELEASE, "beta", TAG), RELEASE["html_url"])
        self.assertEqual(notifier.validate_release(STABLE_RELEASE, "release", STABLE_TAG),
                         STABLE_RELEASE["html_url"])
        for changed in (
            {**RELEASE, "prerelease": False},
            {**RELEASE, "draft": True},
            {**RELEASE, "html_url": "https://example.invalid/release"},
        ):
            with self.assertRaises(ValueError):
                notifier.validate_release(changed, "beta", TAG)
        with self.assertRaises(ValueError):
            notifier.validate_release(RELEASE, "release", TAG)
        with self.assertRaises(ValueError):
            notifier.validate_release(STABLE_RELEASE, "beta", STABLE_TAG)

    def test_webhook_must_be_native_discord_url(self):
        self.assertEqual(notifier.validate_webhook(WEBHOOK), WEBHOOK)
        for value in (WEBHOOK + "/github", WEBHOOK + "?wait=true", "http://" + WEBHOOK[8:],
                      "https://example.invalid/api/webhooks/123/token"):
            with self.assertRaises(ValueError):
                notifier.validate_webhook(value)

    def test_post_suppresses_mentions_and_confirms_message(self):
        for channel, tag, release in (("beta", TAG, RELEASE),
                                      ("release", STABLE_TAG, STABLE_RELEASE)):
            with self.subTest(channel=channel):
                responses = [io.BytesIO(json.dumps(release).encode()), io.BytesIO(b'{"id":"42"}')]
                with mock.patch.dict(os.environ, {"DISCORD_WEBHOOK_URL": WEBHOOK, "GITHUB_TOKEN": "test"}), \
                        mock.patch.object(notifier, "urlopen", side_effect=responses) as open_url, \
                        mock.patch("sys.stdout", new_callable=io.StringIO):
                    notifier.main(channel, tag)
                request = open_url.call_args_list[1][0][0]
                self.assertEqual(request.get_method(), "POST")
                self.assertEqual(request.full_url, WEBHOOK + "?wait=true")
                payload = json.loads(request.data)
                self.assertEqual(payload["allowed_mentions"], {"parse": []})
                self.assertIn(release["html_url"], payload["content"])


if __name__ == "__main__":
    unittest.main()
