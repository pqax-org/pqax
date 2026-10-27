"""python -m pqax collect: the command line of the collector, with the options, messages, and exit codes of pqax collect
in C#. It prints counts only, never the name of a table, and never a token or a secret."""
from __future__ import annotations

import datetime
import os
import re
import sys
import urllib.parse
from typing import Any, Callable, List, Optional, TextIO, Tuple

from .. import __version__
from ..validator import validate
from .collector import CollectionProgress, DetailDepth, collect
from .discovery import DEFAULT_FOLDER_DEPTH, discover, discover_folder, list_items
from .identity import SignInError, TokenCache, access_token, azure_cli, azure_credential, create_credential
from .storage import DEFAULT_ENDPOINT, LocalStorage, OneLakeStorage, StorageError

EXIT_OK = 0
EXIT_INVALID = 1
EXIT_ERROR = 2

_SECRET_OPTIONS = ("--client-secret", "--access-token", "--client-certificate-password")
_VARIABLE = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
_AAD_CODE = re.compile(r"AADSTS\d+")
_UTC = datetime.timezone.utc
#: The name of a filesystem (container) of a storage account: 3 to 63 lowercase letters, digits, and single dashes.
_FILESYSTEM = re.compile(r"[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]")
_DOT = re.compile(r"%2[eE]")
#: A host name of --adls: letters, digits, dashes, dots, and underscores. An IPv6 address is in brackets.
_HOST = re.compile(r"[\w.-]+")
_ADLS_FORM = "--adls takes https://<account>.dfs.core.windows.net/<container>[/<folder>]"

_IDENTITY_MODES = {
    "--device-code": "device-code", "--browser": "browser", "--managed-identity": "managed-identity",
    "--azure-cli": "azure-cli", "--env-credential": "env-credential", "--client-secret-env": "client-secret",
    "--access-token-env": "access-token", "--client-certificate": "client-certificate",
}


class Options:
    """The options of the command, once parsed and checked."""

    def __init__(self) -> None:
        self.local: Optional[str] = None
        self.mount: Optional[str] = None
        self.workspace: Optional[str] = None
        #: The URL of a folder of an ADLS Gen2 container, and what it names: the DFS endpoint of the account, such as
        #: https://<account>.dfs.core.windows.net, the filesystem (container), and the folder, decoded, "" for the
        #: container itself.
        self.adls: Optional[str] = None
        self.adls_endpoint: Optional[str] = None
        self.adls_file_system: Optional[str] = None
        self.adls_folder = ""
        self.items: List[str] = []
        self.output: Optional[str] = None
        self.endpoint: Optional[str] = None
        self.verbose = False
        self.depth = DetailDepth.COLUMN_CHUNKS
        self.level = 2
        self.max_footers = 0
        self.max_commits = 30
        self.parallelism = 16
        self.mode = "device-code"
        self.tenant: Optional[str] = None
        self.client_id: Optional[str] = None
        self.secret_variable: Optional[str] = None
        self.certificate: Optional[str] = None
        self.password_variable: Optional[str] = None
        self.token_variable: Optional[str] = None


def _two_identities(first: Optional[str], second: str) -> str:
    return f"{first} and {second} are two identities; give one."


def _not_a_name(option: str) -> str:
    return (f"{option} takes the name of an environment variable, made of letters, digits, and underscores, such as "
            f"PQAX_SECRET; never the secret itself.")


def parse(args: List[str]) -> Tuple[Optional[Options], Optional[str]]:
    """The options, or None and the problem, as CollectOptions.Parse in C#. args are the arguments after collect."""
    options = Options()
    identity: Optional[str] = None
    i = 0
    while i < len(args):
        arg = args[i]

        def following() -> Optional[str]:
            nonlocal i
            if i + 1 < len(args) and args[i + 1] and not args[i + 1].startswith("--"):
                i += 1
                return args[i]
            return None

        value: Optional[str]
        if arg in ("--local", "--mount", "--workspace", "--adls", "--output", "--tenant", "--client-id", "--endpoint"):
            value = following()
            name = arg[2:].replace("-", "_")
            if name == "client_id":
                options.client_id = value
            else:
                setattr(options, name, value)
        elif arg == "--item":
            value = following()
            if value is not None:
                options.items.append(value)
        elif arg in _IDENTITY_MODES:
            if identity is not None and identity != arg:
                return None, _two_identities(identity, arg)
            identity = arg
            options.mode = _IDENTITY_MODES[arg]
            if arg in ("--client-secret-env", "--access-token-env"):
                value = following()
                if value is not None and not _VARIABLE.fullmatch(value):
                    return None, _not_a_name(arg)
                if arg == "--client-secret-env":
                    options.secret_variable = value
                else:
                    options.token_variable = value
            elif arg == "--client-certificate":
                value = options.certificate = following()
            else:
                value = ""
        elif arg == "--client-certificate-password-env":
            value = options.password_variable = following()
            if value is not None and not _VARIABLE.fullmatch(value):
                return None, _not_a_name(arg)
        elif arg == "--verbose":
            options.verbose = True
            value = ""
        elif arg == "--depth":
            value = following()
            if value is not None:
                depth = {"summary": DetailDepth.SUMMARY, "files": DetailDepth.FILES, "rowgroups": DetailDepth.ROW_GROUPS,
                         "columnchunks": DetailDepth.COLUMN_CHUNKS}.get(value.lower())
                if depth is None:
                    return None, f"unknown depth '{value}'; use summary, files, rowGroups, or columnChunks."
                options.depth = depth
        elif arg == "--level":
            value = following()
            if value is not None:
                if value not in ("1", "2"):
                    return None, "--level is 1 or 2."
                options.level = int(value)
        elif arg in ("--max-footers", "--max-commits", "--parallelism"):
            value = following()
            if value is not None:
                number = int(value) if value.isascii() and value.isdigit() and len(value) <= 10 else None
                if number is not None and number > 0x7FFFFFFF:
                    number = None
                if number is None or (arg == "--parallelism" and number < 1):
                    extra = " of at least 1" if arg == "--parallelism" else ""
                    return None, f"{arg} needs a number{extra}."
                setattr(options, {"--max-footers": "max_footers", "--max-commits": "max_commits",
                                  "--parallelism": "parallelism"}[arg], number)
        else:
            # Never echo what follows an equals sign: it can be a secret.
            name = arg.split("=", 1)[0]
            if name in _SECRET_OPTIONS:
                return None, (f"{name} is not accepted: a secret on the command line stays in the shell history and in "
                              f"the list of processes. Put it in an environment variable and give the name of the "
                              f"variable with {name}-env.")
            return None, f"unknown option '{name}'."
        if value is None:
            return None, f"{arg} needs a value."
        i += 1

    if sum(source is not None for source in (options.local, options.mount, options.workspace, options.adls)) != 1:
        return None, "give one of --local, --mount, --workspace, or --adls."
    if options.adls is not None:
        if options.items:
            return None, "--item does not apply to --adls: the tables are found under the folder of the URL."
        problem, parts = parse_adls(options.adls)
        if parts is None:
            return None, problem
        options.adls_endpoint, options.adls_file_system, options.adls_folder = parts
    if options.workspace is not None and not options.items:
        return None, "--workspace needs at least one --item."
    if options.mount is not None and len(options.items) > 1:
        return None, "--mount reads one item; give at most one --item, which names it."
    if options.output is None:
        return None, "--output is missing."
    if options.endpoint is not None and options.workspace is None:
        return None, "--endpoint applies to --workspace only."
    if (identity is not None or options.tenant is not None or options.client_id is not None
            or options.password_variable is not None) and options.workspace is None and options.adls is None:
        return None, "--tenant, --client-id, and the identity options apply to --workspace and --adls only."
    problem = _check(options, identity)
    return (None, problem) if problem is not None else (options, None)


def parse_adls(url: str) -> Tuple[Optional[str], Optional[Tuple[str, str, str]]]:
    """Splits the URL of --adls into the DFS endpoint of the account, the filesystem, and the folder, as ParseAdls of
    the C# command, or gives the problem. It takes https://<account>.dfs.core.windows.net/<filesystem>[/<folder>] and
    abfss://<filesystem>@<account>.dfs.core.windows.net[/<folder>]. A problem never quotes the URL, which could carry
    a SAS token.

    The URL is read as System.Uri reads it in C#: the scheme and the host in lower case, the default port 443 left out,
    a backslash in the path taken for a slash, the . and .. segments of the path resolved, and the segments decoded."""
    if "?" in url or "#" in url:
        return ("--adls takes a URL without a query: a SAS token is not accepted, since it would stay in the shell "
                "history. Sign in with one of the identities."), None
    try:
        parts = urllib.parse.urlsplit(url.strip())
        host, port = parts.hostname, parts.port
    except ValueError:
        return _ADLS_FORM + ".", None
    user, at, _ = parts.netloc.rpartition("@")
    if (not parts.scheme or not host or (at and parts.scheme != "abfss") or "@" in user
            or not (":" in host or _HOST.fullmatch(host))):
        return _ADLS_FORM + ".", None
    if host.endswith(".blob.core.windows.net"):
        return ("--adls takes the DFS endpoint of the account, <account>.dfs.core.windows.net, not the blob "
                "endpoint."), None
    if host.endswith("fabric.microsoft.com"):
        return "--adls is for a storage account; for OneLake, use --workspace and --item.", None
    # The dot segments are resolved as RFC 3986 says, %2E-encoded too and an empty segment included, before the empty
    # ones are dropped, so none is left to refuse.
    resolved: List[str] = []
    for segment in parts.path.replace("\\", "/").split("/"):
        dots = _DOT.sub(".", segment)
        if dots == "..":
            if resolved:
                resolved.pop()
        elif dots != ".":
            resolved.append(segment)
    segments = [urllib.parse.unquote(segment) for segment in resolved if segment]
    if parts.scheme == "https":
        file_system = segments.pop(0) if segments else None
    elif parts.scheme == "abfss":
        file_system = urllib.parse.unquote(user) if user else None
    else:
        return _ADLS_FORM + ".", None
    if file_system is None:
        return _ADLS_FORM + ": the container is missing.", None
    if not _FILESYSTEM.fullmatch(file_system):
        return "the container of --adls is not a valid name: 3 to 63 lowercase letters, digits, and dashes.", None
    authority = f"[{host}]" if ":" in host else host
    if port is not None and port != 443:
        authority += f":{port}"
    return None, ("https://" + authority, file_system, "/".join(segments))


def _check(options: Options, identity: Optional[str]) -> Optional[str]:
    if options.password_variable is not None and options.mode != "client-certificate":
        return "--client-certificate-password-env goes with --client-certificate."
    mode = options.mode
    if mode in ("client-secret", "client-certificate"):
        return f"{identity} needs --client-id and --tenant." if options.client_id is None or options.tenant is None \
            else None
    if mode == "managed-identity":
        return "--tenant does not apply to --managed-identity: the identity belongs to one tenant." \
            if options.tenant is not None else None
    if mode == "env-credential":
        return ("--env-credential takes the tenant and the client id from AZURE_TENANT_ID and AZURE_CLIENT_ID; leave "
                "out --tenant and --client-id.") if options.tenant is not None or options.client_id is not None else None
    if mode == "access-token":
        return "--tenant and --client-id do not apply to --access-token-env." \
            if options.tenant is not None or options.client_id is not None else None
    return "--client-id goes with --client-secret-env, --client-certificate, or --managed-identity." \
        if options.client_id is not None else None


def usage(stream: TextIO) -> None:
    lines = [
        "Usage:",
        "  python -m pqax collect --local <folder> --output <file.pqax> [--item <name.Kind>]... [options]",
        "  python -m pqax collect --mount <folder> --output <file.pqax> [--item <name.Kind>] [options]",
        "  python -m pqax collect --workspace <name or id> --item <name.Kind> [--item ...] --output <file.pqax> "
        "[identity] [options]",
        "  python -m pqax collect --adls https://<account>.dfs.core.windows.net/<container>[/<folder>] "
        "--output <file.pqax> [identity] [options]",
        "",
        "--local reads a folder with the OneLake layout: <item>.<Kind>/Tables/[<schema>/]<table>. Without --item, every",
        "lakehouse, warehouse, mirrored database, and SQL database in the folder. --mount reads the folder of one item,",
        "which holds Tables, as /lakehouse/default in a Fabric notebook; --item names it, else the folder name does.",
        "--workspace reads OneLake after a sign-in; an item is named as in OneLake, such as Sales.Lakehouse.",
        "Name the workspace and the items both by name or both by id: OneLake refuses a workspace id with item names.",
        "--endpoint <url>, with --workspace, reads another DFS endpoint in the OneLake layout, such as an ADLS Gen2",
        "account, https://<account>.dfs.core.windows.net, with the container as the workspace. Default",
        DEFAULT_ENDPOINT + ".",
        "--adls reads a container of Azure Data Lake Storage Gen2 after a sign-in: a folder with _delta_log is a Delta "
        "table, a folder",
        f"of Parquet files with no subfolder a Parquet table, down to {DEFAULT_FOLDER_DEPTH} levels below the folder. "
        "The identity needs Storage Blob",
        "Data Reader on the container or the account.",
        "",
        "Identity, one of these; a secret is read from an environment variable, never from the command line:",
        "  --access-token-env <NAME>                     An access token for https://storage.azure.com, in the variable NAME.",
        "  --azure-cli                                   The account of az login.",
        "  The others need azure-identity: pip install \"pqax[azure]\".",
        "  (none), --device-code                         A person, with a device code that the command prints. The default.",
        "  --browser                                     A person, in the browser.",
        "  --client-id <id> --client-secret-env <NAME>   A service principal; the secret is in the variable NAME. Needs --tenant.",
        "  --client-id <id> --client-certificate <file>  A service principal with a PFX or PEM certificate. Needs --tenant.",
        "    [--client-certificate-password-env <NAME>]  The password of the certificate, in the variable NAME.",
        "  --managed-identity [--client-id <id>]         The managed identity: system-assigned, or user-assigned with its client id.",
        "  --env-credential                              AZURE_TENANT_ID, AZURE_CLIENT_ID, and AZURE_CLIENT_SECRET or",
        "                                                AZURE_CLIENT_CERTIFICATE_PATH (and AZURE_CLIENT_CERTIFICATE_PASSWORD).",
        "  --tenant <id>                                 The tenant, for a person, a service principal, or the Azure CLI.",
        "",
        "Options:",
        "  --depth summary|files|rowGroups|columnChunks   The detail kept for each table. Default columnChunks.",
        "  --level 1|2                                   1 reads the Delta logs, 2 also the Parquet footers. Default 2.",
        "  --max-footers <n>                             Footers read for each table, 0 for all. Default 0.",
        "  --max-commits <n>                             Commit files read for the history of each table; the commits the state needs are read anyway. Default 30.",
        "  --parallelism <n>                             Storage requests at the same time. Default 16.",
        "  --verbose                                     Print the identity, and every storage request with its status code, to the error output.",
        "",
        "The Delta checkpoints need pyarrow: pip install \"pqax[collect]\".",
        "Exit codes: 0 written and valid, 1 written and invalid, 2 wrong command line or failure.",
    ]
    stream.write("\n".join(lines) + "\n")


def describe_identity(options: Options) -> str:
    tenant = f", tenant {options.tenant}" if options.tenant is not None else ""
    mode = options.mode
    if mode == "browser":
        return f"Sign-in: a person, in the browser{tenant}."
    if mode == "client-secret":
        return f"Sign-in: the service principal {options.client_id}{tenant}, with the client secret of --client-secret-env."
    if mode == "client-certificate":
        ending = " and the password of --client-certificate-password-env." if options.password_variable else "."
        return (f"Sign-in: the service principal {options.client_id}{tenant}, with the certificate "
                f"{os.path.basename(options.certificate or '')}{ending}")
    if mode == "managed-identity":
        return ("Sign-in: the system-assigned managed identity." if options.client_id is None
                else f"Sign-in: the user-assigned managed identity {options.client_id}.")
    if mode == "azure-cli":
        return f"Sign-in: the account of the Azure CLI{tenant}."
    if mode == "access-token":
        return "Sign-in: none; the access token of --access-token-env."
    if mode == "env-credential":
        return ("Sign-in: the service principal of AZURE_CLIENT_ID and AZURE_TENANT_ID, with AZURE_CLIENT_SECRET or "
                "AZURE_CLIENT_CERTIFICATE_PATH.")
    return f"Sign-in: a person, with a device code{tenant}."


def _first_line(error: BaseException) -> str:
    message = str(error).strip()
    return message.splitlines()[0].rstrip() if message else type(error).__name__


def _aad(error: BaseException) -> str:
    messages = []
    current: Optional[BaseException] = error
    while current is not None:
        messages.append(str(current))
        current = current.__cause__ or current.__context__
    match = _AAD_CODE.search(" ".join(messages))
    if match is None:
        return f"the sign-in failed: {_first_line(error)}"
    code = match.group(0)
    causes = {
        "AADSTS7000215": "the client secret is wrong or has expired. Create a new secret for the application and put it "
                         "in the variable.",
        "AADSTS700016": "the client id is not an application of the tenant. Check --client-id and --tenant.",
        "AADSTS7000229": "the application has no service principal in the tenant.",
        "AADSTS90002": "the tenant is not found. Check --tenant.",
        "AADSTS700027": "the certificate is not one that the application has registered, or the clock of this machine "
                        "is wrong.",
        "AADSTS53003": "a Conditional Access policy blocked the sign-in.",
    }
    causes["AADSTS7000222"] = causes["AADSTS7000215"]
    causes["AADSTS900023"] = causes["AADSTS90002"]
    causes["AADSTS700024"] = causes["AADSTS700027"]
    cause = causes.get(code, f"see https://login.microsoftonline.com/error?code={code[6:]}.")
    return f"the sign-in was refused ({code}): {cause}"


def describe(error: BaseException, storage_account: bool = False) -> str:
    """One line with the likely cause of a failed sign-in or a refused token. Never a secret. For a container of a
    storage account, from --adls, a status 401 or 403 names the token and the data role of Azure Storage instead of
    Fabric."""
    name = type(error).__name__
    if isinstance(error, SignInError):
        line = str(error)
    elif storage_account and isinstance(error, StorageError) and error.status == 401:
        line = (f"{str(error).rstrip().rstrip('.')}: the storage account refused the token. An access token must be "
                f"for https://storage.azure.com, from the tenant of the account, and not expired.")
    elif storage_account and isinstance(error, StorageError) and error.status == 403:
        line = (f"{str(error).rstrip().rstrip('.')}: the identity cannot read the container. It needs the role "
                f"Storage Blob Data Reader on the container or the account, or ACLs that give it read and execute on "
                f"the folders; Reader, Contributor, or Owner of the account alone do not read data. A network rule of "
                f"the account also answers 403.")
    elif name == "CredentialUnavailableError":
        line = (f"the identity is not available here: {_first_line(error)} For --managed-identity, the command must run "
                f"where Azure gives the identity; for --azure-cli, run az login first.")
    elif name == "ClientAuthenticationError" or name == "AuthenticationRequiredError":
        line = _aad(error)
    elif isinstance(error, StorageError) and error.status == 401:
        line = (f"{str(error).rstrip().rstrip('.')}: OneLake refused the token. For a service principal or a managed "
                f"identity, the Fabric tenant setting \"Service principals can use Fabric APIs\" must be enabled for it, "
                f"or for a security group it is in. An access token must be for https://storage.azure.com and not "
                f"expired.")
    elif isinstance(error, StorageError) and error.status == 403:
        line = (f"{str(error).rstrip().rstrip('.')}: the identity has no access to the workspace. It needs a workspace "
                f"role that reads OneLake: Contributor, Member, or Admin. Viewer alone does not.")
    elif name == "HttpResponseError":
        line = f"the sign-in failed with status {getattr(error, 'status_code', None)}: {_first_line(error)}"
    else:
        line = f"{name}: {_first_line(error)}"
    return line.replace("\r", " ").replace("\n", " ")


def _required(name: Optional[str], option: str) -> str:
    value = os.environ.get(name or "")
    if not value:
        raise SignInError(f"the environment variable named by {option} is not set, or is empty.")
    return value


def _sign_in(options: Options, error: TextIO) -> Callable[[], Tuple[str, Optional[datetime.datetime]]]:
    """The get_token of the identity of the options."""
    mode = options.mode
    if mode == "access-token":
        token, expires, audience = access_token(_required(options.token_variable, "--access-token-env"))
        if expires is not None and expires <= datetime.datetime.now(_UTC):
            raise SignInError(f"the access token of --access-token-env expired at "
                              f"{expires.strftime('%Y-%m-%d %H:%M:%S')} UTC.")
        if audience is not None and not audience.lower().startswith("https://storage.azure.com"):
            service = "the storage account" if options.adls is not None else "OneLake"
            error.write(f"pqax collect: the access token is for {audience}, and {service} takes one for "
                        f"https://storage.azure.com; it may refuse it.\n")
        return lambda: (token, expires)
    if mode == "azure-cli":
        return azure_cli(options.tenant)
    secret = _required(options.secret_variable, "--client-secret-env") if mode == "client-secret" else None
    password = (_required(options.password_variable, "--client-certificate-password-env")
                if options.password_variable is not None else None)
    credential = create_credential(mode, tenant=options.tenant, client_id=options.client_id, client_secret=secret,
                                   certificate_path=options.certificate, certificate_password=password,
                                   device_code_output=lambda line: (error.write(line + "\n"), error.flush()))
    return azure_credential(credential)


def run(args: List[str], output: Optional[TextIO] = None, error: Optional[TextIO] = None) -> int:
    """Parses the arguments after collect, collects, writes the file, and validates it. 0 when the file is written and
    valid, 1 when the written file is invalid, 2 for a wrong command line or a failure."""
    output = output or sys.stdout
    error = error or sys.stderr
    if any(arg in ("-h", "--help") for arg in args):
        usage(output)
        return EXIT_OK
    options, problem = parse(args)
    if options is None:
        error.write(f"pqax collect: {problem}\n")
        usage(error)
        return EXIT_ERROR
    try:
        from .checkpoint import require_pyarrow
        require_pyarrow()
    except ImportError as missing:
        error.write(f"pqax collect: {missing}\n")
        return EXIT_ERROR
    remote = options.workspace is not None or options.adls is not None
    try:
        return _run(options, output, error)
    except (SignInError, StorageError) as failure:
        if remote and (isinstance(failure, SignInError) or failure.is_access_denied):
            error.write(f"pqax collect: {describe(failure, storage_account=options.adls is not None)}\n")
        else:
            error.write(f"pqax collect: {type(failure).__name__}: {failure}\n")
        return EXIT_ERROR
    except (OSError, ValueError) as failure:
        error.write(f"pqax collect: {type(failure).__name__}: {failure}\n")
        return EXIT_ERROR
    except Exception as failure:  # noqa: BLE001 - the errors of azure-identity, which may not be installed
        if remote and type(failure).__module__.startswith(("azure.", "msal")):
            error.write(f"pqax collect: {describe(failure, storage_account=options.adls is not None)}\n")
            return EXIT_ERROR
        raise


def _run(options: Options, output: TextIO, error: TextIO) -> int:
    storage: Any
    if options.local is not None:
        if not os.path.isdir(options.local):
            error.write("pqax collect: the folder given with --local does not exist.\n")
            return EXIT_ERROR
        storage = LocalStorage(options.local)
        items = options.items or list_items(storage)
    elif options.mount is not None:
        if not os.path.isdir(options.mount):
            error.write("pqax collect: the folder given with --mount does not exist.\n")
            return EXIT_ERROR
        storage = LocalStorage.mount(options.mount, options.items[0] if options.items else None)
        items = [storage.item]
    else:
        if options.verbose:
            error.write(describe_identity(options) + "\n")
        get_token = _sign_in(options, error)
        output.write("Waiting for the sign-in. The token stays in memory and is gone when the command ends.\n"
                     if options.mode in ("device-code", "browser")
                     else "Signing in. The token stays in memory and is gone when the command ends.\n")
        output.flush()
        tokens = TokenCache(get_token)
        tokens.get()
        trace = (lambda line: (error.write(line + "\n"), error.flush())) if options.verbose else None
        if options.adls is not None:
            # A container of a storage account is the filesystem of the same DFS API, at the endpoint of the account.
            storage = OneLakeStorage(options.adls_file_system, tokens, endpoint=options.adls_endpoint, trace=trace)
        else:
            storage = OneLakeStorage(options.workspace, tokens, endpoint=options.endpoint or DEFAULT_ENDPOINT,
                                     trace=trace)
        items = list(options.items)

    if options.adls is not None:
        tables = discover_folder(storage, options.adls_folder)
        output.write(f"Found {len(tables)} tables in {'the folder' if options.adls_folder else 'the container'}.\n")
        output.flush()
        if not tables:
            error.write(f"pqax collect: no table found. A table is a folder with _delta_log, or a folder of Parquet "
                        f"files with no subfolder, at most {DEFAULT_FOLDER_DEPTH} levels below the folder of --adls.\n")
            return EXIT_ERROR
    else:
        tables = []
        for item in items:
            tables.extend(discover(storage, item))
        output.write(f"Found {len(tables)} tables in {len(items)} items.\n")
        output.flush()
        if not tables:
            error.write("pqax collect: no table found. An item is a folder such as Sales.Lakehouse, with a Tables "
                        "folder.\n")
            return EXIT_ERROR

    counts = {"footers": 0, "issues": 0}

    def report(progress: CollectionProgress) -> None:
        counts["footers"] += progress.footers_read
        counts["issues"] += progress.issue_count
        output.write(f"  table {progress.tables_completed} of {progress.table_count}: {progress.footers_read} footers, "
                     f"{progress.issue_count} issues; {progress.request_count} requests so far\n")
        output.flush()

    builder = collect(storage, tables, level=options.level, depth=options.depth, max_footers=options.max_footers,
                      max_commits=options.max_commits, parallelism=options.parallelism, progress=report)
    builder.collection = {"collectorApp": "pqax", "collectorAppVersion": __version__, **builder.collection}
    folder = os.path.dirname(os.path.abspath(options.output))
    if folder:
        os.makedirs(folder, exist_ok=True)
    builder.write(options.output)
    result = validate(options.output)
    verdict = "valid" if result.is_valid else f"invalid, {len(result.issues)} issues"
    output.write(f"{os.path.basename(options.output)}: {len(tables)} tables, {counts['footers']} footers read, "
                 f"{counts['issues']} issues, {builder.collection['requestCount']} requests, "
                 f"{builder.collection['bytesRead']} bytes read; {verdict}\n")
    for issue in result.issues:
        error.write(f"{issue}\n")
    return EXIT_OK if result.is_valid else EXIT_INVALID
