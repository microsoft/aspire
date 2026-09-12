# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

FROM ubuntu:24.04

RUN apt-get update \
    && apt-get install -y --no-install-recommends notcurses-bin \
    && rm -rf /var/lib/apt/lists

ENV LANG=C.UTF-8 \
    TERM=xterm-256color \
    COLORTERM=24bit

USER 1000:1000

# Launch notcurses-demo from the shell after the terminal has attached, so DCP
# does not lose the demo's one-time terminal capability queries during startup.
CMD ["bash", "-i", "-l"]
