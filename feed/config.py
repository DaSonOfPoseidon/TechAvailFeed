import os
from dataclasses import dataclass


@dataclass(frozen=True)
class Config:
    database_url: str
    imap_host: str
    imap_port: int
    imap_user: str
    imap_password: str
    mail_subject: str
    mail_from: str
    processed_label: str
    failed_label: str
    mail_tz: str
    poll_seconds: int
    http_port: int

    @property
    def mail_configured(self) -> bool:
        return bool(self.imap_user and self.imap_password and self.mail_subject)

    @classmethod
    def from_env(cls) -> "Config":
        env = os.environ
        return cls(
            database_url=env["DATABASE_URL"],
            imap_host=env.get("IMAP_HOST", "imap.gmail.com"),
            imap_port=int(env.get("IMAP_PORT", "993")),
            imap_user=env.get("IMAP_USER", ""),
            imap_password=env.get("IMAP_PASSWORD", ""),
            mail_subject=env.get("MAIL_SUBJECT", "TechAvailFeed"),
            mail_from=env.get("MAIL_FROM", ""),
            processed_label=env.get("PROCESSED_LABEL", "techavail-processed"),
            failed_label=env.get("FAILED_LABEL", "techavail-failed"),
            # MBS's local time: block timestamps use it, as does the mail purge's "today".
            mail_tz=env.get("MAIL_TZ", "America/Chicago"),
            poll_seconds=int(env.get("POLL_SECONDS", "60")),
            http_port=int(env.get("HTTP_PORT", "8000")),
        )
