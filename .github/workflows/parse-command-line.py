#!/usr/bin/env python3
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

"""Split CI argument strings as data, never as executable shell source."""

import argparse
import json
import os
import shlex
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--format", choices=("json", "nul"), default="json")
    parser.add_argument("variables", nargs="+", help="Environment variables to split, in order")
    options = parser.parse_args()
    arguments = []
    for variable in options.variables:
        try:
            # For example: --filter-trait "Partition=My Tests" becomes two argv
            # entries. Quotes/backslashes group or escape literals; $(), backticks,
            # semicolons and wildcard characters are never evaluated or expanded.
            arguments.extend(shlex.split(os.environ[variable], posix=True))
        except (KeyError, ValueError) as error:
            parser.error(f"{variable}: {error}")

    if options.format == "nul":
        for argument in arguments:
            sys.stdout.buffer.write(argument.encode("utf-8") + b"\0")
    else:
        print(json.dumps(arguments))


if __name__ == "__main__":
    main()
