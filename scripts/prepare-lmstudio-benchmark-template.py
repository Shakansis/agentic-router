"""Prepare an isolated Qwen template accepting multiple system/developer blocks.

Reads only GGUF metadata. Leaves the vendor template body intact and verifies
unchanged rendering for ordinary conversations; no inference or model download.
"""
import argparse
import hashlib
import json
import struct
from pathlib import Path

from jinja2 import Environment


def extract_template(path):
    with path.open("rb") as file:
        def number(fmt):
            return struct.unpack("<"+fmt, file.read(struct.calcsize(fmt)))[0]

        def string():
            return file.read(number("Q")).decode("utf-8")

        formats = {0: "B", 1: "b", 2: "H", 3: "h", 4: "I", 5: "i", 6: "f", 7: "?", 10: "Q", 11: "q", 12: "d"}

        def skip(kind):
            if kind == 8:
                file.seek(number("Q"), 1)
            elif kind == 9:
                element, count = number("I"), number("Q")
                if element in formats:
                    file.seek(struct.calcsize(formats[element])*count, 1)
                else:
                    for _ in range(count):
                        skip(element)
            else:
                file.seek(struct.calcsize(formats[kind]), 1)

        if file.read(4) != b"GGUF" or number("I") != 3:
            raise ValueError("Expected the existing version-3 GGUF")
        number("Q")
        count = number("Q")
        for _ in range(count):
            key, kind = string(), number("I")
            if key == "tokenizer.chat_template":
                if kind != 8:
                    raise ValueError("Expected a string chat template")
                return string()
            skip(kind)
        raise ValueError("GGUF chat template was not found")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    original = extract_template(args.model)
    prefix = """{%- set blocks = namespace(system=[], conversation=[]) -%}
{%- for item in messages -%}
    {%- if item.role in ['system', 'developer'] -%}
        {%- if item.content is not string -%}
            {{- raise_exception('Benchmark system/developer content must be text.') -}}
        {%- endif -%}
        {%- set blocks.system = blocks.system + [item.content] -%}
    {%- else -%}
        {%- set blocks.conversation = blocks.conversation + [item] -%}
    {%- endif -%}
{%- endfor -%}
{%- if blocks.system -%}
    {%- set messages = [{'role': 'system', 'content': blocks.system | join('\\n\\n')}] + blocks.conversation -%}
{%- else -%}
    {%- set messages = blocks.conversation -%}
{%- endif -%}
"""
    adapted = prefix+original
    env = Environment()
    def reject(message):
        raise ValueError(message)
    env.globals["raise_exception"] = reject
    ordinary = [[dict(role="user", content="Hello")],
                [dict(role="system", content="Instructions"), dict(role="user", content="Hello")],
                [dict(role="system", content="Instructions"), dict(role="user", content="First"),
                 dict(role="assistant", content="Answer"), dict(role="user", content="Next")]]
    render = lambda template, messages: env.from_string(template).render(messages=messages, add_generation_prompt=True)
    if any(render(original, messages) != render(adapted, messages) for messages in ordinary):
        raise ValueError("Ordinary conversation rendering changed")
    multiple = [dict(role="system", content="FIRST-INSTRUCTION"), dict(role="developer", content="SECOND-INSTRUCTION"),
                dict(role="user", content="USER-TASK")]
    result = render(adapted, multiple)
    if not result.index("FIRST-INSTRUCTION") < result.index("SECOND-INSTRUCTION") < result.index("USER-TASK"):
        raise ValueError("Instruction order was not preserved")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.with_suffix(".original.jinja").write_text(original, encoding="utf-8", newline="")
    args.output.write_text(adapted, encoding="utf-8", newline="")
    evidence = dict(source="Existing GGUF tokenizer.chat_template", original_sha256=hashlib.sha256(original.encode()).hexdigest(),
        adapted_sha256=hashlib.sha256(adapted.encode()).hexdigest(), ordinary_renders_unchanged=3,
        multiple_instruction_order_preserved=True, inference_tested=False,
        scope="Loaded benchmark model instance only; stock model and transport payloads remain unchanged.",
        reason="LM Studio maps multiple developer messages to separate system messages rejected by the stock Qwen template.",
        related_issue="https://github.com/lmstudio-ai/lmstudio-bug-tracker/issues/2298")
    args.output.with_suffix(".evidence.json").write_text(json.dumps(evidence, indent=2), encoding="utf-8")
    print(json.dumps(evidence))


if __name__ == "__main__":
    main()
