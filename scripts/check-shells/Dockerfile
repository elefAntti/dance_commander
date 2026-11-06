# An SSH server whose accounts log in to fish and zsh, for the quoting checks (PLAN.md, *Security
# and file-safety fixes from BUGS.md*). atmoz/sftp can't serve these: it forces internal-sftp, so a
# line run on the server never reaches a shell. Started by scripts/check-shells.ps1.
FROM alpine:3.20
RUN apk add --no-cache openssh fish zsh bash \
 && ssh-keygen -A \
 && adduser -D -s /usr/bin/fish fishy && echo 'fishy:commander' | chpasswd \
 && adduser -D -s /bin/zsh zshy && echo 'zshy:commander' | chpasswd \
 && sed -i 's/^#\?PasswordAuthentication.*/PasswordAuthentication yes/' /etc/ssh/sshd_config
CMD ["/usr/sbin/sshd", "-D", "-e"]
