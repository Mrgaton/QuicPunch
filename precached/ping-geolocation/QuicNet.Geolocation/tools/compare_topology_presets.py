#!/usr/bin/env python3
"""Compare frozen runs and fixed-RTT topology ablations. Never sends network probes."""
import argparse
import json
from pathlib import Path

from summarize_presets import distance


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def summarize(directory, label, reference):
    rows = []
    for slug in ("quick", "balanced", "detailed"):
        saved = read(directory / f"{slug}.archive.json")["result"]
        final = saved["final"]
        execution = read(directory / f"{slug}.execution.json")
        observations = saved["session"]["observations"]
        anchors = {a["id"]: a for a in saved["catalog"]["landmarks"]}
        responding = [o for o in observations if o.get("minimumRttMs") is not None]
        responding.sort(key=lambda o: o["minimumRttMs"])
        fastest = [{"id": o["landmarkId"], "name": anchors[o["landmarkId"]].get("name"),
                    "rttMs": o["minimumRttMs"], "region": anchors[o["landmarkId"]].get("regionName")}
                   for o in responding[:8]]
        region = final.get("regionalRefinement", {}).get("calibratedRegion")
        route = final.get("enhanced", {}).get("routeLocation")
        recalculated_file = directory / f"{slug}.topology-recomputed.json"
        recalculated = read(recalculated_file).get("enhanced", {}).get("routeLocation") if recalculated_file.exists() else None
        ablation_file = directory / f"{slug}.without-routes.json"
        ablation = read(ablation_file) if ablation_file.exists() else None
        plain = ablation.get("enhanced", {}).get("routeLocation") if ablation else None
        used_route = route or recalculated
        zones = used_route.get("zones", []) if used_route else []
        row = {"label": label, "preset": slug, "directory": str(directory),
               "seconds": execution["elapsedSeconds"], "phase": final["phase"], "launches": final["sentPackets"],
               "echoAttempts": sum(len(o["samples"]) for o in observations),
               "traceAttemptsRecorded": sum(len(o.get("route", [])) for o in observations),
               "targets": len(observations), "fastest": fastest,
               "traceDestinations": sum(bool(o.get("route")) for o in observations),
               "primaryStatus": final["region"]["status"],
               "cbgStatus": region["status"] if region else None,
               "cbgCenter": region.get("representative") if region else None,
               "cbgExtentKm": region.get("coveringRadiusKm") if region else None,
               "cbgAreaKm2": region.get("areaKm2") if region else None,
               "routeRankingOrigin": "live" if route else "offline-recomputed" if recalculated else None,
               "routeStatus": used_route.get("status") if used_route else None,
               "usableRoutes": used_route.get("usableRoutes") if used_route else None,
               "contrasts": len(used_route.get("contrasts", [])) if used_route else None,
               "zones": zones[:12], "withoutRoutesZones": plain.get("zones", [])[:12] if plain else None,
               "cataloniaRank": next((i+1 for i,z in enumerate(zones) if z["region"] == "Cataluña"), None),
               "limitations": used_route.get("limitations", []) if used_route else []}
        if region and region.get("constraints") and region["status"] == "candidate-region":
            row["evaluationReferenceSatisfiesCbgBounds"] = all(distance(reference, c["center"]) <= c["radiusKm"] for c in region["constraints"])
        if ablation:
            row["cbgUnchangedWithoutRoutes"] = region == ablation.get("regionalRefinement", {}).get("calibratedRegion")
            # Compare the full score vector on exactly the same catalog and RTT samples.
            plain_scores = {(z["countryCode"],z["region"]): z["relativeScore"] for z in plain.get("zones", [])}
            row["maximumScoreChangeFromRoutes"] = max((abs(z["relativeScore"] - plain_scores.get((z["countryCode"],z["region"]),0)) for z in zones), default=0)
        rows.append(row)
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("after", type=Path)
    parser.add_argument("--before", type=Path, required=True)
    parser.add_argument("--historical", type=Path)
    args = parser.parse_args()
    reference = {"latitude":41.3874,"longitude":2.1686}
    rows = summarize(args.before,"before-current-connection",reference) + summarize(args.after,"after-topology",reference)
    if args.historical:
        rows += summarize(args.historical,"historical-mobile-reported-by-user",reference)
    output = {"evaluationReferenceOnly": reference, "referenceNeverSuppliedToLocator": True,
              "caution": "Live before/after runs occur at different times. Historical mobile data are not the same connection as the current baseline. Relative route scores are not probabilities; CBG extent is not an error radius.",
              "results": rows}
    destination = args.after / "topology-comparison.json"
    destination.write_text(json.dumps(output,ensure_ascii=False,indent=2),encoding="utf-8")
    for row in rows:
        print(json.dumps({k:row.get(k) for k in ("label","preset","seconds","traceDestinations","usableRoutes","contrasts","cataloniaRank","cbgExtentKm","cbgUnchangedWithoutRoutes","maximumScoreChangeFromRoutes")},ensure_ascii=False))
    print(destination)


if __name__ == "__main__":
    main()
