"""The access tokens of OneLake: a token given as it is, such as the one of a notebook, the account of the Azure CLI,
and the credentials of the Azure SDK, which need azure-identity, the extra pqax[azure].

A get_token callable returns (token, expires_on), expires_on an aware datetime or None; TokenCache turns one into the
token provider of OneLakeStorage, which asks for a new token only shortly before the last one expires. No token or
secret is ever written to a file, a trace, or the footprint.
"""
from __future__ import annotations

import base64
import datetime
import json
import os
import shutil
import subprocess
import threading
from typing import Any, Callable, Optional, Tuple

from .storage import SCOPE

__all__ = [
    "SignInError", "TokenCache", "access_token", "azure_cli", "azure_credential", "create_credential",
    "require_azure_identity",
]

_UTC = datetime.timezone.utc
_MARGIN = datetime.timedelta(minutes=5)
_RESOURCE = "https://storage.azure.com"

GetToken = Callable[[], Tuple[str, Optional[datetime.datetime]]]


class SignInError(Exception):
    """A sign-in that cannot work as given, with a message that holds no secret."""


class TokenCache:
    """A token provider over a get_token callable: it keeps the token until five minutes before it expires. Safe from
    several threads; calling the instance returns the token."""

    def __init__(self, get_token: GetToken, clock: Optional[Callable[[], datetime.datetime]] = None) -> None:
        if get_token is None:
            raise TypeError("get_token is missing.")
        self._get_token = get_token
        self._clock = clock or (lambda: datetime.datetime.now(_UTC))
        self._lock = threading.Lock()
        self._token: Optional[str] = None
        self._expires: Optional[datetime.datetime] = None

    def get(self) -> str:
        """The token, asked for again when there is none or it expires within five minutes."""
        with self._lock:
            if self._token is None or (self._expires is not None and self._expires < self._clock() + _MARGIN):
                self._token, self._expires = self._get_token()
            return self._token

    __call__ = get


def _claims(token: str) -> Optional[dict]:
    segments = token.split(".")
    if len(segments) != 3:
        return None
    try:
        payload = segments[1].replace("-", "+").replace("_", "/")
        payload += "=" * (-len(payload) % 4)
        document = json.loads(base64.b64decode(payload, validate=True).decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        return None
    return document if isinstance(document, dict) else None


def access_token(value: str) -> Tuple[str, Optional[datetime.datetime], Optional[str]]:
    """A token given as it is, such as the one of notebookutils.credentials.getToken("storage"): the token, when it
    expires, from its exp claim, and its audience, from its aud claim, both None when the token does not say."""
    if not value:
        raise ValueError("The token is empty.")
    expires: Optional[datetime.datetime] = None
    audience: Optional[str] = None
    claims = _claims(value)
    if claims is not None:
        exp = claims.get("exp")
        if type(exp) is int:
            try:
                expires = datetime.datetime.fromtimestamp(exp, _UTC)
            except (OverflowError, OSError, ValueError):
                expires = None
        aud = claims.get("aud")
        if isinstance(aud, str):
            audience = aud
        elif isinstance(aud, list) and aud and isinstance(aud[0], str):
            audience = aud[0]
    return value, expires, audience


def azure_cli(tenant: Optional[str] = None, *, executable: Optional[str] = None,
              timeout: float = 60.0) -> GetToken:
    """A get_token over the account of az login: az account get-access-token for https://storage.azure.com."""
    def get_token() -> Tuple[str, Optional[datetime.datetime]]:
        program = executable or shutil.which("az") or shutil.which("az.cmd")
        if program is None:
            raise SignInError("the Azure CLI is not installed, or az is not on the PATH. Install it and run az login, "
                              "or give another identity.")
        command = [program, "account", "get-access-token", "--resource", _RESOURCE, "--output", "json"]
        if tenant:
            command += ["--tenant", tenant]
        try:
            completed = subprocess.run(command, capture_output=True, text=True, timeout=timeout,
                                       encoding="utf-8", errors="replace")
        except (OSError, subprocess.SubprocessError) as error:
            raise SignInError(f"the Azure CLI could not run: {type(error).__name__}.") from None
        if completed.returncode != 0:
            lines = [line.strip() for line in (completed.stderr or "").splitlines() if line.strip()]
            detail = lines[0] if lines else f"exit code {completed.returncode}"
            raise SignInError(f"the Azure CLI gave no token: {detail} Run az login first.")
        try:
            document = json.loads(completed.stdout)
            token = document["accessToken"]
        except (ValueError, KeyError, TypeError):
            raise SignInError("the Azure CLI answered without a token.") from None
        expires: Optional[datetime.datetime] = None
        if type(document.get("expires_on")) is int:
            expires = datetime.datetime.fromtimestamp(document["expires_on"], _UTC)
        elif isinstance(document.get("expiresOn"), str):
            try:
                expires = datetime.datetime.fromisoformat(document["expiresOn"]).astimezone(_UTC)
            except ValueError:
                expires = None
        return token, expires

    return get_token


def require_azure_identity(option: str = "this identity"):
    """The module azure.identity, or SignInError naming the extra that installs it."""
    try:
        import azure.identity as identity
    except ImportError:
        raise SignInError(f"{option} needs the package azure-identity: pip install \"pqax[azure]\". "
                          f"--access-token-env and --azure-cli need nothing more.") from None
    return identity


def azure_credential(credential: Any) -> GetToken:
    """A get_token over any TokenCredential of azure-identity, for the scope of OneLake."""
    def get_token() -> Tuple[str, Optional[datetime.datetime]]:
        token = credential.get_token(SCOPE)
        expires = datetime.datetime.fromtimestamp(token.expires_on, _UTC) if token.expires_on else None
        return token.token, expires

    return get_token


def create_credential(mode: str, *, tenant: Optional[str] = None, client_id: Optional[str] = None,
                      client_secret: Optional[str] = None, certificate_path: Optional[str] = None,
                      certificate_password: Optional[str] = None,
                      device_code_output: Optional[Callable[[str], None]] = None) -> Any:
    """A credential of azure-identity: mode is device-code, browser, client-secret, client-certificate,
    managed-identity, or env-credential, as the options of pqax collect name them."""
    identity = require_azure_identity("--" + mode)
    if mode == "browser":
        return identity.InteractiveBrowserCredential(tenant_id=tenant)
    if mode == "client-secret":
        return identity.ClientSecretCredential(tenant, client_id, client_secret)
    if mode == "client-certificate":
        if not os.path.isfile(certificate_path or ""):
            raise SignInError("the certificate file of --client-certificate does not exist.")
        return identity.CertificateCredential(tenant, client_id, certificate_path, password=certificate_password)
    if mode == "managed-identity":
        return identity.ManagedIdentityCredential(client_id=client_id) if client_id \
            else identity.ManagedIdentityCredential()
    if mode == "env-credential":
        values = {name: os.environ.get(name) for name in (
            "AZURE_TENANT_ID", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET", "AZURE_CLIENT_CERTIFICATE_PATH",
            "AZURE_CLIENT_CERTIFICATE_PASSWORD")}
        for name in ("AZURE_TENANT_ID", "AZURE_CLIENT_ID"):
            if not values[name]:
                raise SignInError(f"--env-credential needs {name}, which is not set or is empty.")
        if values["AZURE_CLIENT_SECRET"]:
            return identity.ClientSecretCredential(values["AZURE_TENANT_ID"], values["AZURE_CLIENT_ID"],
                                                   values["AZURE_CLIENT_SECRET"])
        path = values["AZURE_CLIENT_CERTIFICATE_PATH"]
        if not path:
            raise SignInError("--env-credential needs AZURE_CLIENT_SECRET or AZURE_CLIENT_CERTIFICATE_PATH, and "
                              "neither is set.")
        if not os.path.isfile(path):
            raise SignInError("the certificate file of AZURE_CLIENT_CERTIFICATE_PATH does not exist.")
        return identity.CertificateCredential(values["AZURE_TENANT_ID"], values["AZURE_CLIENT_ID"], path,
                                              password=values["AZURE_CLIENT_CERTIFICATE_PASSWORD"] or None)
    # The device code is the default: a browser window can open where nobody sees it, and the code is printed here.
    def prompt(verification_uri: str, user_code: str, expires_on: Any) -> None:
        message = (f"To sign in, use a web browser to open the page {verification_uri} and enter the code "
                   f"{user_code} to authenticate.")
        (device_code_output or print)(message)

    return identity.DeviceCodeCredential(tenant_id=tenant, prompt_callback=prompt)
