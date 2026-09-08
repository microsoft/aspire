# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

FROM ubuntu:24.04

# Ubuntu installs BB outside the container's default PATH.
RUN apt-get update \
    && apt-get install -y --no-install-recommends bb \
    && ln -s /usr/games/bb /usr/local/bin/bb \
    && rm -rf /var/lib/apt/lists

ENV LANG=C.UTF-8 \
    TERM=xterm-256color \
    COLORTERM=24bit

USER 1000:1000

# Start bb manually after attaching to the terminal, like the Notcurses demo.
CMD ["bash", "-i", "-l"]
