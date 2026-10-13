"""Per-day detection metrics from replay readings.csv files, for before/after comparisons.

    dotnet run --project ha-addon -c Release -- replay <snapshots> --out <dir> --timestamps-utc --no-debug-images
    python tools/replay_metrics.py <dirA> [<dirB> ...]

Per day (frames that are not dark): the share of detected / no_jar / no_surface frames, the share of
detected frames with an implausible jar column (wider than 700 px, narrower than 380 px, touching a
frame border), and the spread of the reported glass bottom (rows, most common 10 px bins), which
should be one value per unmoved scene.
"""
import sys
import pandas as pd


def load(folder: str) -> pd.DataFrame:
    r = pd.read_csv(f"{folder}/readings.csv")
    r["t"] = pd.to_datetime(r["time"], utc=True)
    r["day"] = r["t"].dt.strftime("%m-%d")
    r["wid"] = r["jar_right_px"] - r["jar_left_px"]
    return r


def summarize(r: pd.DataFrame) -> pd.DataFrame:
    live = r[r["outcome"] != "dark_frame"]
    detected = live[live["outcome"] == "detected"]
    bad = detected[(detected["wid"] > 700) | (detected["wid"] < 380)
                   | (detected["jar_right_px"] > 1190) | (detected["jar_left_px"] < 90)]
    table = live.groupby("day").agg(
        frames=("outcome", "size"),
        detected=("outcome", lambda s: (s == "detected").mean()),
        no_jar=("outcome", lambda s: (s == "no_jar").mean()),
        no_surface=("outcome", lambda s: (s == "no_surface").mean()),
    )
    table["bad_column"] = bad.groupby("day").size() / table["frames"]
    bins = (detected["jar_bottom_px"] // 10 * 10).astype("Int64")
    table["bottom_bins"] = detected.assign(bin=bins).groupby("day")["bin"].agg(
        lambda s: dict(s.value_counts().head(3)))
    return table.fillna(0)


if __name__ == "__main__":
    pd.set_option("display.width", 200)
    for folder in sys.argv[1:]:
        print(folder)
        print(summarize(load(folder)).round(2).to_string())
        print()
