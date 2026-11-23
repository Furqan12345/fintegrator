# Deploy SimpleIPaaS on Oracle Cloud Always Free

This uses the single-server Docker setup in the repository for a private demo
or internal pilot. The API, Engine, and web server run on one Arm VM and share
a local SQLite volume. Customer self-service needs stronger user authentication
and isolation than this deployment provides.

## 1. Create the VM

In your Oracle Cloud home region, create an **Always Free-eligible** Ubuntu
24.04 Arm instance with shape `VM.Standard.A1.Flex`, 2 OCPUs, and 12 GB RAM.
Keep the default 50 GB boot volume. Select a public subnet and public IPv4
address. Add your SSH public key. Do not select a paid shape or storage tier.

Oracle's Always Free allowance for A1 is 2 OCPUs and 12 GB RAM in total,
plus 200 GB of combined boot and block volumes in the home region. A1 capacity
may be unavailable in a particular availability domain; try another domain or
later. See [Oracle's current limits](https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm).

In the instance's subnet security list or network security group, allow
inbound TCP 80 and 443 from the internet. Allow TCP 22 only from your own IP.
On the VM, check the Ubuntu firewall too. Do not expose API port 8080.

Point a domain's A record at the VM's public IP. The current Caddy setup needs
a hostname to obtain an HTTPS certificate.

## 2. Install Docker on the VM

SSH to the VM using the username shown for its Ubuntu image, usually `ubuntu`.
Install Docker Engine and the Compose plugin using
[Docker's Ubuntu instructions](https://docs.docker.com/engine/install/ubuntu/).
Confirm `docker compose version` works. If you add your user to the `docker`
group, sign out and back in before continuing.

## 3. Copy this workspace to the VM

The deployment files in this workspace include uncommitted changes, so a
fresh Git clone will not contain them yet. From this Windows machine, copy the
repository's `Dockerfile`, `docker-compose.yml`, `SimpleIPaaS.slnx`, `src`,
and `ops/azure` directory into `~/fintegrator` on the VM. Do not copy local
`.env` files or credentials. The Docker build ignores generated `bin` and
`obj` directories.

If you later commit and push the deployment changes, cloning that revision on
the VM is also an option.

## 4. Configure and start

From `~/fintegrator` on the VM, follow
[the shared setup steps](../azure/README.md#2-prepare-secrets-on-the-vm)
to create `.env`, generate the API and encryption keys, and set the Caddy
password. Then follow its **Build and start** and **Backup and updates**
sections. The same Compose file and Caddy configuration work on Arm because
the images are built on the VM for its architecture.

Check that `https://YOUR_DOMAIN/health/ready` responds after login, then run
one safe test flow. Keep a copy of `.env` and backups outside the VM.

## Limits

This repository has passed .NET build, tests, and local API smoke checks on
Windows. Its Docker images have not yet been built or tested on an Arm VM.
Oracle account creation, identity checks, VM provisioning, DNS changes, and
the first live deployment still require account access.
