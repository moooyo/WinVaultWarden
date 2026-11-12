"""Preserve stable resource IDs when adding newly inventoried UI text."""

import argparse
import json
from pathlib import Path


def load(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def merge(original, incoming):
    texts = {entry["Key"]: entry for entry in original["Texts"]}
    for entry in incoming["Texts"]:
        key = entry["Key"]
        if key in texts and texts[key]["Source"] != entry["Source"]:
            raise ValueError(f"Resource hash collision: {key}")
        if key not in texts:
            texts[key] = entry
            continue
        contexts = texts[key]["Contexts"] + entry["Contexts"]
        unique = {json.dumps(item, sort_keys=True): item for item in contexts}
        texts[key]["Contexts"] = list(unique.values())
    elements = {(item["File"], item["Uid"]): item for item in original["XamlElements"]}
    for item in incoming["XamlElements"]:
        elements[item["File"], item["Uid"]] = item
    result = dict(original)
    result["Texts"] = sorted(texts.values(), key=lambda entry: entry["Key"])
    result["XamlElements"] = [entry for entry in elements.values() if entry["Element"].split(":")[-1] != "Window"]
    result["Issues"] = incoming["Issues"]
    result["CSharpEligibleOccurrences"] = incoming["CSharpEligibleOccurrences"]
    result["XamlEligibleProperties"] = incoming["XamlEligibleProperties"]
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("original")
    parser.add_argument("incoming")
    parser.add_argument("output")
    arguments = parser.parse_args()
    result = merge(load(arguments.original), load(arguments.incoming))
    Path(arguments.output).parent.mkdir(parents=True, exist_ok=True)
    Path(arguments.output).write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Merged catalog: {len(result['Texts'])} strings and {len(result['XamlElements'])} XAML IDs.")
