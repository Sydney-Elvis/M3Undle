#!/usr/bin/env python3

import argparse
import json
import sys
import urllib.parse
import urllib.request


def player_api(base_url: str, username: str, password: str, action: str, **values):
    query = urllib.parse.urlencode(
        {"username": username, "password": password, "action": action, **values}
    )
    request = urllib.request.Request(
        f"{base_url.rstrip('/')}/player_api.php?{query}",
        headers={"User-Agent": "M3Undle-Xtream-Vod-Series-Smoke/1.0"},
    )
    with urllib.request.urlopen(request, timeout=20) as response:
        if response.status != 200:
            raise RuntimeError(f"{action} returned HTTP {response.status}")
        return json.load(response)


def require(condition: bool, message: str):
    if not condition:
        raise AssertionError(message)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Validate Xtream VOD and series metadata against a running M3Undle instance."
    )
    parser.add_argument("--base-url", required=True)
    parser.add_argument("--username", required=True)
    parser.add_argument("--password", required=True)
    args = parser.parse_args()

    vod_streams = player_api(args.base_url, args.username, args.password, "get_vod_streams")
    require(isinstance(vod_streams, list), "get_vod_streams did not return an array")
    if vod_streams:
        vod = vod_streams[0]
        vod_info = player_api(
            args.base_url,
            args.username,
            args.password,
            "get_vod_info",
            vod_id=str(vod["stream_id"]),
        )
        movie_data = vod_info.get("movie_data", {})
        require(str(movie_data.get("stream_id")) == str(vod["stream_id"]), "VOD ID did not round-trip")
        require(bool(movie_data.get("container_extension")), "VOD container extension is missing")
        require(isinstance(vod_info.get("info"), dict), "VOD info object is missing")

    series = player_api(args.base_url, args.username, args.password, "get_series")
    require(isinstance(series, list), "get_series did not return an array")
    if series:
        series_info = player_api(
            args.base_url,
            args.username,
            args.password,
            "get_series_info",
            series_id=str(series[0]["series_id"]),
        )
        episodes = series_info.get("episodes", {})
        require(isinstance(episodes, dict), "series episodes object is missing")
        flattened = [episode for season in episodes.values() for episode in season]
        require(bool(flattened), "selected series has no episodes")
        for episode in flattened:
            require(isinstance(episode.get("info"), dict), "episode info object is missing")
            require(bool(episode.get("container_extension")), "episode container extension is missing")
            require(
                episode.get("direct_source", "").endswith(f".{episode['container_extension']}"),
                "episode direct_source extension does not match container_extension",
            )

    print(f"PASS: {len(vod_streams)} VOD items and {len(series)} series inspected")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(f"FAIL: {error}", file=sys.stderr)
        sys.exit(1)
