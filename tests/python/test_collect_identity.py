"""pqax.collect.identity: the expiry and the audience of an access token, the cache of tokens until five minutes before
they expire, and the token functions over a credential of azure-identity and over the Azure CLI (the interface of the
Python collector, which follows AccessTokenCredential and TokenCache of the C# command). Standard library only: the
credential is a fake with the shape of an azure-identity TokenCredential.

An expiry is given to TokenCache as an aware datetime, or None; azure_credential converts the expires_on of an
azure-identity AccessToken, seconds since 1970, to one.
"""
from __future__ import annotations

import collections
import datetime
import threading
import time

import pytest

from collect_kit import epoch_seconds, jwt
from pqax.collect.identity import TokenCache, access_token, azure_cli, azure_credential

AccessToken = collections.namedtuple("AccessToken", "token expires_on")
SCOPE = "https://storage.azure.com/.default"


# ---- access tokens ----

def test_a_jwt_gives_its_expiry_and_its_audience():
    expiry = datetime.datetime(2030, 1, 2, 3, 4, 5, tzinfo=datetime.timezone.utc)
    token = jwt({"aud": "https://storage.azure.com/", "exp": int(expiry.timestamp()), "sub": "synthetic"})
    value, expires_on, audience = access_token(token)
    assert value == token
    assert isinstance(expires_on, datetime.datetime)
    assert epoch_seconds(expires_on) == expiry.timestamp()
    assert audience == "https://storage.azure.com/"


def test_a_jwt_without_exp_never_expires_and_one_without_aud_has_no_audience():
    token = jwt({"sub": "synthetic"})
    assert access_token(token) == (token, None, None)


@pytest.mark.parametrize("token", ["not-a-jwt", "a.b.c", "opaque~token_0123456789", "eyJhbGciOiJub25lIn0.%%%."])
def test_a_token_that_is_not_a_jwt_never_expires(token):
    assert access_token(token) == (token, None, None)


# ---- the cache ----

class Source:
    """A get_token that counts its calls and gives "token-<call>", expiring after the seconds given."""

    def __init__(self, lifetime=3600.0, fail_first=False, gate=None):
        self.lifetime = lifetime
        self.fail_first = fail_first
        self.gate = gate
        self.calls = 0
        self._lock = threading.Lock()

    def __call__(self):
        with self._lock:
            self.calls += 1
            call = self.calls
        if self.gate is not None:
            self.gate.wait(10)
        if self.fail_first and call == 1:
            raise RuntimeError("synthetic failure")
        if self.lifetime is None:
            return f"token-{call}", None
        return f"token-{call}", datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(seconds=self.lifetime)


def test_a_valid_token_is_requested_once():
    source = Source(3600)
    cache = TokenCache(source)
    assert cache.get() == "token-1"
    assert cache.get() == "token-1"
    assert cache() == "token-1"  # an instance is a token provider
    assert source.calls == 1


def test_a_token_that_expires_in_less_than_five_minutes_is_not_kept():
    source = Source(120)
    cache = TokenCache(source)
    assert cache.get() == "token-1"
    assert cache.get() == "token-2"
    assert source.calls == 2


def test_a_token_that_expires_in_six_minutes_is_kept():
    source = Source(360)
    cache = TokenCache(source)
    cache.get()
    assert cache.get() == "token-1"
    assert source.calls == 1


def test_a_token_without_expiry_is_kept():
    source = Source(None)
    cache = TokenCache(source)
    cache.get()
    assert cache.get() == "token-1"
    assert source.calls == 1


def test_a_failure_of_the_source_reaches_the_caller_and_a_later_call_tries_again():
    cache = TokenCache(Source(3600, fail_first=True))
    with pytest.raises(RuntimeError):
        cache.get()
    assert cache.get() == "token-2"


def test_concurrent_callers_share_one_request():
    gate = threading.Event()
    source = Source(3600, gate=gate)
    cache = TokenCache(source)
    results = []
    threads = [threading.Thread(target=lambda: results.append(cache.get())) for _ in range(16)]
    for thread in threads:
        thread.start()
    time.sleep(0.2)
    gate.set()
    for thread in threads:
        thread.join(30)
    assert results == ["token-1"] * 16
    assert source.calls == 1


# ---- credentials ----

class FakeCredential:
    """The shape of an azure-identity TokenCredential: get_token(*scopes) returns an AccessToken."""

    def __init__(self, token="credential-token", expires_on=None):
        self.token = token
        self.expires_on = expires_on if expires_on is not None else int(time.time()) + 3600
        self.scopes = []

    def get_token(self, *scopes, **kwargs):
        self.scopes.append(scopes)
        return AccessToken(self.token, self.expires_on)


def test_a_credential_is_asked_for_the_scope_of_onelake():
    credential = FakeCredential()
    get_token = azure_credential(credential)
    token, expires_on = get_token()
    assert token == "credential-token"
    assert isinstance(expires_on, datetime.datetime) and expires_on.utcoffset() == datetime.timedelta(0)
    assert epoch_seconds(expires_on) == credential.expires_on
    assert credential.scopes == [(SCOPE,)]


def test_a_credential_in_a_cache_is_asked_once():
    credential = FakeCredential()
    cache = TokenCache(azure_credential(credential))
    assert cache() == cache() == "credential-token"
    assert len(credential.scopes) == 1


def test_the_azure_cli_without_az_on_the_path_fails(tmp_path, monkeypatch):
    empty = tmp_path / "empty-path"
    empty.mkdir()
    monkeypatch.setenv("PATH", str(empty))
    get_token = azure_cli()
    with pytest.raises(Exception):
        get_token()
    get_token = azure_cli(tenant="00000000-1111-2222-3333-444444444444")
    with pytest.raises(Exception):
        get_token()
