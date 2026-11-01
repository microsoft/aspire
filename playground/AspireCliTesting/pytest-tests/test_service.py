# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

import os
from urllib.request import urlopen

import pytest


def test_service_response():
    with urlopen(os.environ["PYTEST_SERVICE_URL"].rstrip("/") + "/", timeout=30) as response:
        assert response.status == 200
        assert response.read().decode("utf-8") == "Aspire CLI testing"


def test_service_health():
    with urlopen(os.environ["PYTEST_SERVICE_URL"].rstrip("/") + "/health", timeout=30) as response:
        assert response.status == 200


def test_controlled_failure():
    assert os.environ["PYTEST_PLAYGROUND_FAIL"].lower() != "true", "Opt-in pytest playground failure."


@pytest.mark.skip(reason="Demonstrates a skipped pytest result in the common summary.")
def test_skipped_example():
    pass
