# CinemaWatch server configuration (Ansible)

`site.yml` brings a fresh Ubuntu 26.04 VPS to the state recorded in [`docs/SAAS_INFRA.md`](../../docs/SAAS_INFRA.md)
§1.3: the stage 2 hardening as code, plus swap, Docker and the app's folder. It runs **on the server itself**
(local connection), so no control machine, no WSL2 and no SSH key in GitHub are needed
([`docs/SAAS.md`](../../docs/SAAS.md) §9).

| Part (tag) | What it does |
| --- | --- |
| `updates` | `apt` full upgrade; base packages; time zone `Etc/UTC`; daily security updates with a reboot at 03:30 UTC when needed (`20auto-upgrades`, `52unattended-upgrades-local`, Docker's origin included) |
| `ssh` | `/etc/ssh/sshd_config.d/00-hardening.conf`: key-only login, no root login; checked with `sshd -t` before the reload. Refuses to run while the admin user has no key in `authorized_keys` |
| `firewall` | `ufw`: incoming denied except 22/tcp, outgoing allowed, logging low |
| `swap` | `/swapfile` (2 GB), in `/etc/fstab`, `vm.swappiness=10` |
| `docker` | Docker's release key (fingerprint checked), Docker's repository, Docker Engine and the Compose plugin; `/etc/docker/daemon.json` (log limits, `live-restore`) |
| `app` | `/opt/cinemawatch` (root only, `0750`) for the compose file and `.env` (deployment, stage 4.4) |

## Run it

Server (bash, over SSH), as `ubuntu` (passwordless sudo); the full procedure after a reinstall is
[`docs/SAAS_INFRA.md`](../../docs/SAAS_INFRA.md) §4.13:

```bash
sudo apt update && sudo apt install -y ansible git python3-debian
git clone https://github.com/damiankowalski/CinemaWatch.git ~/CinemaWatch
cd ~/CinemaWatch/infra/ansible
ansible-playbook site.yml --check --diff
ansible-playbook site.yml
```

The first command of the two is a dry run: it shows what would change and changes nothing (before the first real
run it skips installing Docker, because the repository is not there yet). Expected at the end of the real run:
`failed=0`. Run it a second time: `changed=0` means the server already matches the playbook. Later, after
`git -C ~/CinemaWatch pull`, the same two commands apply changes; `--tags ssh` (etc.) runs one part only.

`ansible.cfg` is read only from a folder that is not writable by everyone, which the clone in the home folder is not.

CI checks the playbook with the Ubuntu 26.04 packages (`ansible`, `ansible-lint`): `--syntax-check` and
`ansible-lint --profile production`.
